using System.Net.Http;
using System.Text;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Orchestrates autonomous Developer+Reviewer pairs for backlog phases.
/// One active dev-review pair at a time per project.
/// All state mutations are synchronized via _lock. Event callbacks from CLI
/// background threads acquire the lock before mutating state.
/// Events are deferred while _lock is held and flushed after release
/// (via RaiseLog/RaiseEvent + FlushDeferredEvents), so subscribers can
/// safely call back into the orchestrator without deadlock.
/// </summary>
public class TeamOrchestratorService : IDisposable
{
    private readonly BacklogService _backlogService;
    private readonly GitService _gitService;

    private string? _claudeExePath;
    private string? _workingDirectory;

    // All mutable state is guarded by _lock
    private readonly object _lock = new();
    private DevReviewSession? _activeSession;
    private OrchestratorState _state = OrchestratorState.Stopped;
    private bool _softPauseRequested;
    private int _reviewAttempt;
    private string? _lastReviewCriticalSnippet;
    private const int MaxReviewRetries = 3;

    // Health monitoring (Phase 6)
    private System.Timers.Timer? _healthTimer;
    private const int HealthCheckIntervalMs = 10_000;
    private const int StallNudgeThresholdSeconds = 60;
    private const int StallKillThresholdSeconds = 300;
    private const int MaxSessionRetries = 3;
    private int _healthTickCount;
    private int _internetCheckPending; // 0=idle, 1=in-flight (Interlocked)

    // Deferred events — buffered while _lock is held, flushed after release.
    // Prevents deadlock risk from synchronous subscribers calling back.
    private readonly List<Action> _deferredEvents = [];

    // Events for TeamViewModel to consume
    public event Action<string>? OnLog;
    public event Action<OrchestratorState>? OnStateChanged;
    public event Action<string, string>? OnPhaseStarted;         // featureId, phaseId
    public event Action<string, string, PhaseStatus>? OnPhaseCompleted; // featureId, phaseId, status
    public event Action<string>? OnDevTextDelta;
    public event Action<string>? OnReviewTextDelta;
    public event Action<string>? OnError;
    public event Action<IReadOnlyList<SessionHealthInfo>>? OnHealthSnapshot;

    public OrchestratorState State { get { lock (_lock) return _state; } }
    public bool IsRunning { get { lock (_lock) return _state == OrchestratorState.Running; } }
    public bool IsPaused { get { lock (_lock) return _state is OrchestratorState.SoftPaused or OrchestratorState.HardPaused; } }

    public TeamOrchestratorService(BacklogService backlogService, GitService gitService)
    {
        _backlogService = backlogService;
        _gitService = gitService;
    }

    // ── Deferred event helpers ──────────────────────────────────────
    // Events are buffered while _lock is held (Monitor.IsEntered) and
    // fired after the lock is released via FlushDeferredEvents().

    private void RaiseLog(string msg) => RaiseEvent(() => OnLog?.Invoke(msg));
    private void RaiseStateChanged(OrchestratorState s) => RaiseEvent(() => OnStateChanged?.Invoke(s));
    private void RaisePhaseStarted(string fid, string pid) => RaiseEvent(() => OnPhaseStarted?.Invoke(fid, pid));
    private void RaisePhaseCompleted(string fid, string pid, PhaseStatus s) => RaiseEvent(() => OnPhaseCompleted?.Invoke(fid, pid, s));
    private void RaiseError(string msg) => RaiseEvent(() => OnError?.Invoke(msg));
    private void RaiseHealthSnapshot(IReadOnlyList<SessionHealthInfo> items) => RaiseEvent(() => OnHealthSnapshot?.Invoke(items));

    private void RaiseEvent(Action action)
    {
        if (Monitor.IsEntered(_lock))
            _deferredEvents.Add(action);
        else
            action();
    }

    /// <summary>
    /// Fire all buffered events. Must be called after releasing _lock.
    /// </summary>
    private void FlushDeferredEvents()
    {
        while (true)
        {
            List<Action> batch;
            lock (_lock)
            {
                if (_deferredEvents.Count == 0) return;
                batch = [.. _deferredEvents];
                _deferredEvents.Clear();
            }
            foreach (var e in batch) e();
        }
    }

    public void Configure(string claudeExePath, string? workingDirectory)
    {
        lock (_lock)
        {
            _claudeExePath = claudeExePath;
            _workingDirectory = workingDirectory;
        }
    }

    /// <summary>
    /// Start the orchestrator. Picks up next pending phase.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_state is OrchestratorState.Running
                or OrchestratorState.SoftPaused or OrchestratorState.HardPaused)
                return;

            _softPauseRequested = false;
            SetStateLocked(OrchestratorState.Running);
            StartHealthTimerLocked();
            TryStartNextPhaseLocked();
        }
        FlushDeferredEvents();
    }

    /// <summary>
    /// Notify the orchestrator that new work may be available (e.g. a feature was just planned).
    /// If in WaitingForWork state, re-checks for pending phases.
    /// </summary>
    public void NotifyNewWork()
    {
        lock (_lock)
        {
            if (_state != OrchestratorState.WaitingForWork)
                return;

            if (string.IsNullOrEmpty(_workingDirectory)
                || _backlogService.GetNextPendingPhase(_workingDirectory) == null)
                return;

            SetStateLocked(OrchestratorState.Running);
            TryStartNextPhaseLocked();
        }
        FlushDeferredEvents();
    }

    /// <summary>
    /// Soft pause: let current dev+review cycle finish, don't start next.
    /// </summary>
    public void SoftPause()
    {
        lock (_lock)
        {
            if (_state != OrchestratorState.Running && _state != OrchestratorState.WaitingForWork)
                return;

            _softPauseRequested = true;

            if (_activeSession == null)
            {
                SetStateLocked(OrchestratorState.SoftPaused);
            }
            else
            {
                RaiseLog("Soft pause requested. Current phase will finish.");
            }
        }
        FlushDeferredEvents();
    }

    /// <summary>
    /// Hard pause: stop current session immediately. Phase reverts to Pending for resume.
    /// </summary>
    public void HardPause()
    {
        lock (_lock)
        {
            _softPauseRequested = false;
            if (_activeSession != null)
                RaiseLog($"Hard pause: interrupted [{_activeSession.PhaseTitle}]. Will retry on resume.");
            StopActiveSessionLocked(markFailed: false);
            SetStateLocked(OrchestratorState.HardPaused);
        }
        FlushDeferredEvents();
    }

    /// <summary>
    /// Resume from paused state.
    /// </summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (_state is not (OrchestratorState.SoftPaused or OrchestratorState.HardPaused))
                return;

            _softPauseRequested = false;
            SetStateLocked(OrchestratorState.Running);
            StartHealthTimerLocked();
            RaiseLog("Checking internet before resuming...");
        }
        FlushDeferredEvents();

        _ = Task.Run(async () =>
        {
            var ok = await IsInternetAvailableAsync();
            try
            {
                lock (_lock)
                {
                    if (_state != OrchestratorState.Running) return;
                    if (!ok)
                    {
                        RaiseLog("Resume aborted: no internet connection.");
                        RaiseError("Cannot resume: api.anthropic.com unreachable");
                        StopHealthTimerLocked();
                        SetStateLocked(OrchestratorState.HardPaused);
                        return;
                    }
                    RaiseLog("Internet OK. Resuming.");
                    TryStartNextPhaseLocked();
                }
            }
            finally { FlushDeferredEvents(); }
        });
    }

    /// <summary>
    /// Stop orchestrator completely. Kills active session.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            StopHealthTimerLocked();
            _softPauseRequested = false;
            StopActiveSessionLocked(markFailed: false);
            SetStateLocked(OrchestratorState.Stopped);
        }
        FlushDeferredEvents();
    }

    public void Dispose()
    {
        Stop();
    }

    // ── State machine (must be called under _lock) ─────────────────

    private void SetStateLocked(OrchestratorState newState)
    {
        if (_state == newState) return;
        _state = newState;
        RaiseStateChanged(newState);
    }

    // ── Core flow (must be called under _lock) ─────────────────────

    private void TryStartNextPhaseLocked()
    {
        if (_state != OrchestratorState.Running) return;
        if (_activeSession != null) return;
        if (string.IsNullOrEmpty(_workingDirectory)) return;

        var next = _backlogService.GetNextPendingPhase(_workingDirectory);
        if (next == null)
        {
            SetStateLocked(OrchestratorState.WaitingForWork);
            RaiseLog("No pending phases. Waiting for new work.");
            return;
        }

        var (feature, phase) = next.Value;

        _backlogService.MarkPhaseStatus(feature.Id, phase.Id, PhaseStatus.InProgress);

        if (feature.Status is FeatureStatus.Planned or FeatureStatus.Raw)
            _backlogService.MarkFeatureStatus(feature.Id, FeatureStatus.InProgress);

        _reviewAttempt = 0;
        _lastReviewCriticalSnippet = null;

        LaunchDeveloperLocked(feature, phase);
    }

    // ── Developer session (must be called under _lock) ─────────────

    private void LaunchDeveloperLocked(BacklogFeature feature, BacklogPhase phase)
    {
        var session = new DevReviewSession
        {
            FeatureId = feature.Id,
            PhaseId = phase.Id,
            FeatureTitle = feature.Title ?? feature.RawIdea,
            PhaseTitle = phase.Title,
            PhasePlan = phase.Plan,
            AcceptanceCriteria = phase.AcceptanceCriteria
        };

        var devCli = new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = feature.ProjectPath ?? _workingDirectory,
            SystemPrompt = TeamPrompts.BuildDeveloperSystemPrompt(
                session.FeatureTitle, phase.Title,
                phase.Plan, phase.AcceptanceCriteria),
            DangerouslySkipPermissions = true
        };

        session.DevCli = devCli;
        _activeSession = session;

        WireDevEvents(session);

        RaiseLog($"Dev started: [{session.FeatureTitle}] Phase {phase.Order}: {phase.Title}");
        RaisePhaseStarted(feature.Id, phase.Id);

        var prompt = $"Execute the following phase plan:\n\n{phase.Plan}";
        if (!string.IsNullOrEmpty(phase.AcceptanceCriteria))
            prompt += $"\n\nAcceptance criteria:\n{phase.AcceptanceCriteria}";

        devCli.SendMessage(prompt);
    }

    private void WireDevEvents(DevReviewSession session)
    {
        var cli = session.DevCli!;

        cli.OnTextDelta += text =>
        {
            session.DevResponse.Append(text);
            OnDevTextDelta?.Invoke(text);
        };

        cli.OnSessionStarted += (sessionId, _, _) =>
        {
            lock (_lock) session.DevSessionId = sessionId;
        };

        cli.OnFileChanged += filePath =>
        {
            lock (_lock)
            {
                if (!session.ChangedFiles.Contains(filePath))
                    session.ChangedFiles.Add(filePath);
            }
        };

        cli.OnCompleted += result =>
        {
            lock (_lock)
            {
                if (_activeSession != session) return; // stale callback
                if (result.SessionId is not null)
                    session.DevSessionId = result.SessionId;

                HandleDevCompletedLocked(session);
            }
            FlushDeferredEvents();
        };

        cli.OnError += error =>
        {
            lock (_lock)
            {
                if (_activeSession != session) return; // stale callback
                HandleDevErrorLocked(session, error);
            }
            FlushDeferredEvents();
        };

        cli.OnRateLimitDetected += () =>
        {
            lock (_lock)
            {
                if (_activeSession != session) return;
                _softPauseRequested = true;
                RaiseLog("Rate limit detected. Will pause after current phase completes.");
                RaiseError("Rate limit detected — orchestrator will soft-pause after this phase");
            }
            FlushDeferredEvents();
        };

        cli.OnStreamStalled += seconds =>
        {
            lock (_lock)
            {
                if (_activeSession != session) return;
                session.DevStallSeconds = seconds;
            }
        };

        cli.OnStreamResumed += () =>
        {
            lock (_lock)
            {
                if (_activeSession != session) return;
                session.DevStallSeconds = 0;
            }
        };
    }

    // ── Dev completion (called under _lock) ────────────────────────

    private void HandleDevCompletedLocked(DevReviewSession session)
    {
        _backlogService.MarkPhaseStatus(
            session.FeatureId, session.PhaseId,
            PhaseStatus.InReview,
            changedFiles: session.ChangedFiles.Count > 0 ? session.ChangedFiles : null);

        session.DevCli?.StopSession();
        session.CurrentPhase = SessionPhase.Review;

        RaiseLog($"Dev completed [{session.PhaseTitle}]. Starting review...");

        // Run git diff outside lock to avoid blocking Pause/Stop
        _ = Task.Run(() => PrepareAndLaunchReview(session));
    }

    private void HandleDevErrorLocked(DevReviewSession session, string error)
    {
        RaiseLog($"Dev error [{session.PhaseTitle}]: {error}");
        RaiseError($"Developer session failed: {error}");

        _backlogService.MarkPhaseStatus(
            session.FeatureId, session.PhaseId, PhaseStatus.Failed,
            errorMessage: error);

        RaisePhaseCompleted(session.FeatureId, session.PhaseId, PhaseStatus.Failed);

        CleanupSessionLocked();
        AfterPhaseEndedLocked();
    }

    // ── Review session ─────────────────────────────────────────────

    /// <summary>
    /// Prepares review context outside lock (blocking I/O), then launches review under lock.
    /// </summary>
    private void PrepareAndLaunchReview(DevReviewSession session)
    {
        try
        {
            // Gather data outside lock to avoid blocking Pause/Stop/health
            var workDir = session.DevCli?.WorkingDirectory ?? _workingDirectory;
            var gitDiff = _gitService.RunGit("diff HEAD", workDir);
            var devSummary = session.DevResponse.ToString();
            var changedFiles = new List<string>(session.ChangedFiles);

            lock (_lock)
            {
                if (_activeSession != session) return;
                LaunchReviewLocked(session, workDir, devSummary, changedFiles, gitDiff);
            }
            FlushDeferredEvents();
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (_activeSession != session) return;
                RaiseLog($"Review launch failed: {ex.Message}");
                RaiseError($"Review launch failed for [{session.PhaseTitle}]: {ex.Message}");
                // Treat as consensus to avoid blocking the pipeline
                HandleReviewCompletedLocked(session, "", ReviewVerdict.Consensus);
            }
            FlushDeferredEvents();
        }
    }

    private void LaunchReviewLocked(DevReviewSession session, string? workDir,
        string devSummary, List<string> changedFiles, string? gitDiff)
    {
        var reviewer = new ReviewService();
        reviewer.Configure(_claudeExePath ?? "claude", workDir);
        session.Reviewer = reviewer;

        var context = TeamPrompts.BuildOrchestratorReviewContext(
            session.FeatureTitle,
            session.PhaseTitle,
            devSummary,
            changedFiles,
            gitDiff);

        reviewer.OnTextDelta += text =>
        {
            session.ReviewResponse.Append(text);
            OnReviewTextDelta?.Invoke(text);
        };

        reviewer.OnReviewCompleted += (fullText, verdict) =>
        {
            lock (_lock)
            {
                if (_activeSession != session) return; // stale callback
                HandleReviewCompletedLocked(session, fullText, verdict);
            }
            FlushDeferredEvents();
        };

        reviewer.OnError += error =>
        {
            lock (_lock)
            {
                if (_activeSession != session) return;
                RaiseLog($"Review error [{session.PhaseTitle}]: {error}");
                // Treat review error as consensus (don't block progress)
                HandleReviewCompletedLocked(session, "", ReviewVerdict.Consensus);
            }
            FlushDeferredEvents();
        };

        reviewer.OnStreamStalled += seconds =>
        {
            lock (_lock)
            {
                if (_activeSession != session) return;
                session.ReviewStallSeconds = seconds;
            }
        };

        reviewer.OnStreamResumed += () =>
        {
            lock (_lock)
            {
                if (_activeSession != session) return;
                session.ReviewStallSeconds = 0;
                session.NudgeSent = false;
            }
        };

        reviewer.RunReview(context);
    }

    // ── Review completion (called under _lock) ─────────────────────

    private void HandleReviewCompletedLocked(DevReviewSession session, string reviewText, ReviewVerdict verdict)
    {
        session.Reviewer?.Stop();
        session.Reviewer = null;

        if (verdict == ReviewVerdict.Consensus)
        {
            var commitHash = _gitService.RunGit("rev-parse --short HEAD", _workingDirectory)?.Trim();

            _backlogService.MarkPhaseStatus(
                session.FeatureId, session.PhaseId, PhaseStatus.Done,
                summary: TruncateSummary(session.DevResponse.ToString()),
                changedFiles: session.ChangedFiles,
                commitHash: commitHash);

            RaiseLog($"Phase [{session.PhaseTitle}] completed with CONSENSUS.");
            RaisePhaseCompleted(session.FeatureId, session.PhaseId, PhaseStatus.Done);

            CleanupSessionLocked();
            AfterPhaseEndedLocked();
            return;
        }

        // ISSUES_FOUND or None
        _reviewAttempt++;

        if (_reviewAttempt >= MaxReviewRetries)
        {
            RaiseLog($"Phase [{session.PhaseTitle}] failed review after {_reviewAttempt} retries.");

            _backlogService.MarkPhaseStatus(
                session.FeatureId, session.PhaseId, PhaseStatus.Failed,
                errorMessage: $"Review failed after {_reviewAttempt} retries");

            RaisePhaseCompleted(session.FeatureId, session.PhaseId, PhaseStatus.Failed);

            CleanupSessionLocked();
            AfterPhaseEndedLocked();
            return;
        }

        // Loop detection
        var criticalSnippet = ExtractFirstCritical(reviewText);
        if (criticalSnippet is not null && criticalSnippet == _lastReviewCriticalSnippet)
        {
            RaiseLog($"Review loop detected for [{session.PhaseTitle}]. Marking done with warning.");

            _backlogService.MarkPhaseStatus(
                session.FeatureId, session.PhaseId, PhaseStatus.Done,
                summary: TruncateSummary(session.DevResponse.ToString()) +
                    "\n[Review loop detected - passed with warning]",
                changedFiles: session.ChangedFiles);

            RaisePhaseCompleted(session.FeatureId, session.PhaseId, PhaseStatus.Done);

            CleanupSessionLocked();
            AfterPhaseEndedLocked();
            return;
        }
        _lastReviewCriticalSnippet = criticalSnippet;

        // Send fix prompt to developer
        RaiseLog($"Review round {_reviewAttempt}/{MaxReviewRetries} for [{session.PhaseTitle}]. Sending fixes...");

        session.CurrentPhase = SessionPhase.FixingIssues;
        session.DevResponse.Clear();
        session.ReviewResponse.Clear();

        var fixPrompt = $"""
            A code reviewer found issues in your work:

            {reviewText}

            Please fix all the issues identified above. After fixing, provide a summary of changes.
            """;

        // Re-create dev CLI and resume the session
        var workDir = session.DevCli?.WorkingDirectory ?? _workingDirectory;
        session.DevCli?.StopSession();

        var devCli = new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = workDir,
            SystemPrompt = TeamPrompts.BuildDeveloperSystemPrompt(
                session.FeatureTitle, session.PhaseTitle,
                session.PhasePlan, session.AcceptanceCriteria),
            DangerouslySkipPermissions = true
        };

        if (session.DevSessionId is not null)
            devCli.RestoreSession(session.DevSessionId);
        else
            RaiseLog("Warning: no session ID for fix retry, developer context may be lost.");

        session.DevCli = devCli;
        WireDevEvents(session);

        devCli.SendMessage(fixPrompt);
    }

    // ── After phase ends (called under _lock) ──────────────────────

    private void AfterPhaseEndedLocked()
    {
        if (_softPauseRequested)
        {
            _softPauseRequested = false;
            SetStateLocked(OrchestratorState.SoftPaused);
        }
        else if (_state == OrchestratorState.Running)
        {
            TryStartNextPhaseLocked();
        }
    }

    // ── Session cleanup (called under _lock) ───────────────────────

    private void StopActiveSessionLocked(bool markFailed)
    {
        if (_activeSession == null) return;

        _activeSession.DevCli?.StopSession();
        _activeSession.Reviewer?.Stop();

        if (markFailed)
        {
            _backlogService.MarkPhaseStatus(
                _activeSession.FeatureId,
                _activeSession.PhaseId,
                PhaseStatus.Failed,
                errorMessage: "Orchestrator stopped");
        }
        else
        {
            // Revert to Pending so it can be picked up again on resume
            _backlogService.MarkPhaseStatus(
                _activeSession.FeatureId,
                _activeSession.PhaseId,
                PhaseStatus.Pending);
        }

        _activeSession = null;
    }

    private void CleanupSessionLocked()
    {
        _activeSession?.DevCli?.StopSession();
        _activeSession?.Reviewer?.Stop();
        _activeSession = null;
    }

    // ── Health monitoring (Phase 6) ─────────────────────────────────

    /// <summary>
    /// Start health timer. Must be called under _lock.
    /// </summary>
    private void StartHealthTimerLocked()
    {
        StopHealthTimerLocked();
        Interlocked.Exchange(ref _healthTickCount, 0);
        _healthTimer = new System.Timers.Timer(HealthCheckIntervalMs);
        _healthTimer.Elapsed += (_, _) => _ = HealthTickAsync();
        _healthTimer.AutoReset = true;
        _healthTimer.Start();
    }

    /// <summary>
    /// Stop health timer. Must be called under _lock.
    /// </summary>
    private void StopHealthTimerLocked()
    {
        _healthTimer?.Stop();
        _healthTimer?.Dispose();
        _healthTimer = null;
    }

    private async Task HealthTickAsync()
    {
        try
        {
            await HealthTickCoreAsync();
        }
        catch (Exception ex)
        {
            // Prevent unobserved Task exceptions from being silently lost
            RaiseLog($"Health check error: {ex.Message}");
        }
    }

    private async Task HealthTickCoreAsync()
    {
        var tick = Interlocked.Increment(ref _healthTickCount);

        // Internet check every 6th tick (~60s), non-overlapping
        if (tick % 6 == 0 && Interlocked.CompareExchange(ref _internetCheckPending, 1, 0) == 0)
        {
            bool hasInternet;
            try { hasInternet = await IsInternetAvailableAsync(); }
            finally { Interlocked.Exchange(ref _internetCheckPending, 0); }

            if (!hasInternet)
            {
                lock (_lock)
                {
                    if (_state is OrchestratorState.Running or OrchestratorState.WaitingForWork)
                    {
                        RaiseLog("Internet lost. Hard pausing all sessions.");
                        RaiseError("Auto-paused: no internet connection detected");
                        _softPauseRequested = false;
                        if (_activeSession != null)
                            RaiseLog($"Hard pause: interrupted [{_activeSession.PhaseTitle}].");
                        StopActiveSessionLocked(markFailed: false);
                        StopHealthTimerLocked();
                        SetStateLocked(OrchestratorState.HardPaused);
                    }
                }
                FlushDeferredEvents();
                return;
            }
        }

        DevReviewSession? session;
        lock (_lock) { session = _activeSession; }

        if (session == null)
        {
            RaiseHealthSnapshot([]);
            return;
        }

        try
        {
            lock (_lock)
            {
                if (_activeSession != session) return;

                // 1. Dev process dead check
                if (session.CurrentPhase is SessionPhase.Development or SessionPhase.FixingIssues
                    && session.DevCli is not null
                    && !session.DevCli.IsProcessRunning)
                {
                    RaiseLog("Dev process found dead by health check. Restarting.");
                    RestartDevSessionLocked(session, "process died");
                    return;
                }

                // 2. Dev stall threshold → kill and restart
                if (session.CurrentPhase is SessionPhase.Development or SessionPhase.FixingIssues
                    && session.DevStallSeconds >= StallKillThresholdSeconds)
                {
                    RaiseLog($"Dev stalled {session.DevStallSeconds}s (>={StallKillThresholdSeconds}s). Restarting.");
                    session.DevCli?.StopSession();
                    session.DevStallSeconds = 0;
                    RestartDevSessionLocked(session, "stall timeout");
                    return;
                }

                // 3. Review stall: nudge at 60s, kill at 300s (treat as consensus)
                if (session.CurrentPhase == SessionPhase.Review)
                {
                    if (session.ReviewStallSeconds >= StallKillThresholdSeconds)
                    {
                        RaiseLog($"Reviewer stalled {session.ReviewStallSeconds}s. Treating as consensus.");
                        HandleReviewCompletedLocked(session, "", ReviewVerdict.Consensus);
                        return;
                    }
                    if (session.ReviewStallSeconds >= StallNudgeThresholdSeconds && !session.NudgeSent)
                    {
                        session.NudgeSent = true;
                        session.Reviewer?.SendNudge();
                        RaiseLog("Reviewer nudged after 60s inactivity.");
                    }
                }

                FireHealthSnapshotLocked(session);
            }
        }
        finally { FlushDeferredEvents(); }
    }

    private void RestartDevSessionLocked(DevReviewSession session, string reason)
    {
        session.RetryCount++;
        if (session.RetryCount > MaxSessionRetries)
        {
            RaiseLog($"Dev restart limit ({MaxSessionRetries}) exceeded ({reason}). Failing phase.");
            RaiseError($"Phase [{session.PhaseTitle}] failed: {reason} after {MaxSessionRetries} retries");

            _backlogService.MarkPhaseStatus(session.FeatureId, session.PhaseId,
                PhaseStatus.Failed, errorMessage: $"Dev failed: {reason}");
            RaisePhaseCompleted(session.FeatureId, session.PhaseId, PhaseStatus.Failed);

            CleanupSessionLocked();
            AfterPhaseEndedLocked();
            return;
        }

        RaiseLog($"Restarting dev session (attempt {session.RetryCount}/{MaxSessionRetries}, reason: {reason}).");

        var workDir = session.DevCli?.WorkingDirectory ?? _workingDirectory;
        session.DevCli?.StopSession();
        session.DevStallSeconds = 0;

        var devCli = new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = workDir,
            SystemPrompt = TeamPrompts.BuildDeveloperSystemPrompt(
                session.FeatureTitle, session.PhaseTitle,
                session.PhasePlan, session.AcceptanceCriteria),
            DangerouslySkipPermissions = true
        };

        if (session.DevSessionId is not null)
            devCli.RestoreSession(session.DevSessionId);
        else
            RaiseLog("Warning: no session ID for restart, dev context may be partial.");

        session.DevCli = devCli;
        session.CurrentPhase = SessionPhase.Development;
        WireDevEvents(session);

        devCli.SendMessage("Please continue with the previous phase plan. Summarize what was done and complete remaining tasks.");
    }

    private void FireHealthSnapshotLocked(DevReviewSession session)
    {
        var items = new List<SessionHealthInfo>();
        var elapsed = $"{(DateTime.Now - session.StartedAt).TotalMinutes:F0}m";

        if (session.CurrentPhase is SessionPhase.Development or SessionPhase.FixingIssues)
        {
            var health = session.DevCli is null || !session.DevCli.IsProcessRunning
                ? SessionHealth.Error
                : session.DevStallSeconds >= StallNudgeThresholdSeconds
                    ? SessionHealth.Stalled
                    : SessionHealth.Healthy;

            var detail = session.DevStallSeconds >= StallNudgeThresholdSeconds
                ? $"stalled {session.DevStallSeconds}s" : "";

            var role = session.CurrentPhase == SessionPhase.FixingIssues ? "Dev (fixing)" : "Dev";
            items.Add(new SessionHealthInfo(role, health, detail, elapsed));
        }

        if (session.CurrentPhase == SessionPhase.Review)
        {
            var health = session.ReviewStallSeconds >= StallNudgeThresholdSeconds
                ? SessionHealth.Stalled
                : SessionHealth.Healthy;

            var detail = session.ReviewStallSeconds >= StallNudgeThresholdSeconds
                ? $"stalled {session.ReviewStallSeconds}s" : "";

            items.Add(new SessionHealthInfo("Review", health, detail, elapsed));
        }

        RaiseHealthSnapshot(items);
    }

    private static readonly HttpClient s_httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static async Task<bool> IsInternetAvailableAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, "https://api.anthropic.com");
            await s_httpClient.SendAsync(req);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Utilities ──────────────────────────────────────────────────

    private static string TruncateSummary(string text)
    {
        if (text.Length <= 500) return text;

        var sepIdx = text.LastIndexOf("---", StringComparison.Ordinal);
        if (sepIdx > 0 && text.Length - sepIdx < 1000)
            return text[(sepIdx + 3)..].Trim();

        return text[^500..];
    }

    private static string? ExtractFirstCritical(string reviewText)
    {
        var lines = reviewText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("CRITICAL", StringComparison.OrdinalIgnoreCase))
            {
                var snippet = string.Join(" ", lines.Skip(i).Take(3))
                    .Trim().ToLowerInvariant();
                return snippet.Length > 200 ? snippet[..200] : snippet;
            }
        }
        return null;
    }
}

// ── Supporting types ───────────────────────────────────────────────

public enum OrchestratorState
{
    Stopped,
    Running,
    SoftPaused,
    HardPaused,
    WaitingForWork
}

public class DevReviewSession
{
    public required string FeatureId;
    public required string PhaseId;
    public required string FeatureTitle;
    public required string PhaseTitle;
    public string PhasePlan = "";
    public string? AcceptanceCriteria;

    // Developer CLI
    public ClaudeCliService? DevCli;
    public StringBuilder DevResponse = new();
    public List<string> ChangedFiles = [];
    public string? DevSessionId;

    // Reviewer
    public ReviewService? Reviewer;
    public StringBuilder ReviewResponse = new();

    // Timing
    public DateTime StartedAt = DateTime.Now;
    public SessionPhase CurrentPhase = SessionPhase.Development;

    // Health tracking (Phase 6)
    public int DevStallSeconds;
    public int ReviewStallSeconds;
    public bool NudgeSent;
    public int RetryCount;
}

public enum SessionPhase
{
    Development,
    Review,
    FixingIssues
}
