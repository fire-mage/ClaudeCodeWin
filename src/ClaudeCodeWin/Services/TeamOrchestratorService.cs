using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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
    private static readonly Regex BugWordRegex = new(@"\bBUG\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly BacklogService _backlogService;
    private readonly GitService _gitService;

    private string? _claudeExePath;
    private string? _workingDirectory;

    public TeamNotesService? TeamNotesService { get; set; }

    // All mutable state is guarded by _lock
    private readonly object _lock = new();
    private DevReviewSession? _activeSession;
    private OrchestratorState _state = OrchestratorState.Stopped;
    private bool _softPauseRequested;
    private int _reviewAttempt;
    private string? _lastReviewCriticalSnippet;
    private int _maxReviewRetries = 11;

    // Health monitoring (Phase 6)
    private System.Timers.Timer? _healthTimer;
    private const int HealthCheckIntervalMs = 10_000;
    private const int StallNudgeThresholdSeconds = 60;
    private const int StallDisplayThresholdSeconds = 60; // visual health indicator (lower than nudge)
    // Stall detector (no output for N seconds), NOT an absolute timeout.
    private int _reviewStallKillThresholdSeconds = 660;
    private int _devStallNudgeThresholdSeconds = 360;
    private int _devStallKillThresholdSeconds = 660;
    private int _maxSessionRetries = 2;
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

    // Structured events for rich team chat rendering
    public event Action? OnDevTextBlockStart;
    public event Action<string>? OnDevThinkingDelta;
    public event Action<string, string, string>? OnDevToolUseStarted;  // toolName, toolUseId, input
    public event Action<string, string, string>? OnDevToolResult;       // toolName, toolUseId, content
    public event Action? OnDevCompleted;
    public event Action<string>? OnDevError;
    public event Action? OnReviewCompleted;
    public event Action<string>? OnReviewError;
    public event Action<IReadOnlyList<SessionHealthInfo>>? OnHealthSnapshot;
    public event Action<string?>? OnActiveTaskChanged; // featureId or null when cleared
    public event Action<bool>? OnSoftPauseRequested; // true = requested, false = cleared

    public OrchestratorState State { get { lock (_lock) return _state; } }
    public bool IsRunning { get { lock (_lock) return _state == OrchestratorState.Running; } }
    public bool IsPaused { get { lock (_lock) return _state is OrchestratorState.SoftPaused or OrchestratorState.HardPaused; } }
    public bool IsSoftPauseRequested { get { lock (_lock) return _softPauseRequested; } }

    public string? GetActiveFeatureId() { lock (_lock) return _activeSession?.FeatureId; }

    public List<string> GetActiveSessionChangedFiles()
    {
        lock (_lock)
        {
            return _activeSession?.ChangedFiles.ToList() ?? [];
        }
    }

    public (string devText, string reviewText, SessionPhase phase) GetActiveSessionText()
    {
        lock (_lock)
        {
            if (_activeSession == null)
                return ("", "", SessionPhase.Development);
            return (
                _activeSession.DevResponse.ToString(),
                _activeSession.ReviewResponse.ToString(),
                _activeSession.CurrentPhase);
        }
    }

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
    private void RaiseActiveTaskChanged(string? featureId) => RaiseEvent(() => OnActiveTaskChanged?.Invoke(featureId));
    private void RaiseDevTextBlockStart() => RaiseEvent(() => OnDevTextBlockStart?.Invoke());
    private void RaiseDevThinkingDelta(string text) => RaiseEvent(() => OnDevThinkingDelta?.Invoke(text));
    private void RaiseDevToolUseStarted(string n, string id, string inp) => RaiseEvent(() => OnDevToolUseStarted?.Invoke(n, id, inp));
    private void RaiseDevToolResult(string n, string id, string c) => RaiseEvent(() => OnDevToolResult?.Invoke(n, id, c));
    private void RaiseDevCompleted() => RaiseEvent(() => OnDevCompleted?.Invoke());
    private void RaiseDevError(string err) => RaiseEvent(() => OnDevError?.Invoke(err));
    private void RaiseReviewCompleted() => RaiseEvent(() => OnReviewCompleted?.Invoke());
    private void RaiseReviewError(string err) => RaiseEvent(() => OnReviewError?.Invoke(err));

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

    public void Configure(string claudeExePath, string? workingDirectory, AppSettings? settings = null)
    {
        lock (_lock)
        {
            _claudeExePath = claudeExePath;
            _workingDirectory = workingDirectory;

            if (settings is not null)
            {
                _maxReviewRetries = settings.ReviewAutoRetries;
                _reviewStallKillThresholdSeconds = settings.ReviewTimeoutSeconds;
                _devStallKillThresholdSeconds = settings.DevTimeoutSeconds;
                // Ensure nudge fires before kill; clamp to at least 60s before timeout
                _devStallNudgeThresholdSeconds = Math.Min(settings.DevNudgeSeconds,
                    Math.Max(settings.DevTimeoutSeconds - 60, 60));
                _maxSessionRetries = settings.DevStallMaxRetries;
            }
        }
    }

    /// <summary>
    /// Transition from Stopped to WaitingForWork without starting the health timer
    /// or picking up work immediately. Used for auto-start on project open.
    /// </summary>
    public void StartReady()
    {
        lock (_lock)
        {
            if (_state != OrchestratorState.Stopped) return;

            _softPauseRequested = false;
            SetStateLocked(OrchestratorState.WaitingForWork);
            RaiseLog("Orchestrator ready. Waiting for work.");
        }
        FlushDeferredEvents();
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
            StartHealthTimerLocked();
            TryStartNextPhaseLocked();
        }
        FlushDeferredEvents();
    }

    /// <summary>
    /// Clears a pending soft-pause request without changing state.
    /// Use when the caller that requested SoftPauseAsync was cancelled before
    /// the orchestrator transitioned to SoftPaused — prevents the orchestrator
    /// from transitioning to SoftPaused after the current phase finishes.
    /// </summary>
    public void ClearPendingSoftPause()
    {
        lock (_lock)
        {
            _softPauseRequested = false;
            RaiseEvent(() => OnSoftPauseRequested?.Invoke(false));
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
                RaiseEvent(() => OnSoftPauseRequested?.Invoke(true));
            }
        }
        FlushDeferredEvents();
    }

    /// <summary>
    /// Async soft pause: requests soft pause and waits until state actually transitions
    /// to SoftPaused, HardPaused, or Stopped. Returns immediately if already in one of those states.
    /// Callers MUST provide a meaningful CancellationToken — without one, this can hang
    /// indefinitely if the active session dies without triggering AfterPhaseEndedLocked.
    /// </summary>
    /// <exception cref="OperationCanceledException">The <paramref name="ct"/> was cancelled.</exception>
    public async Task SoftPauseAsync(CancellationToken ct)
    {
        // If caller passes an uncancellable token, apply a default timeout
        // to prevent indefinite hangs if the active session dies silently.
        CancellationTokenSource? fallbackCts = null;
        if (!ct.CanBeCanceled)
        {
            fallbackCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            ct = fallbackCts.Token;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(OrchestratorState newState)
        {
            if (newState is OrchestratorState.SoftPaused
                or OrchestratorState.HardPaused
                or OrchestratorState.Stopped)
            {
                tcs.TrySetResult();
            }
        }

        // Subscribe BEFORE checking state to close the race window where another
        // thread transitions state between our check and subscription.
        OnStateChanged += OnChanged;
        try
        {
            lock (_lock)
            {
                if (_state is OrchestratorState.SoftPaused
                    or OrchestratorState.HardPaused
                    or OrchestratorState.Stopped)
                {
                    tcs.TrySetResult();
                }
            }

            if (!tcs.Task.IsCompleted)
            {
                SoftPause();

                // Defensive re-check: SoftPause fires OnStateChanged synchronously
                // via FlushDeferredEvents before returning, so TCS is normally already
                // set here. This guards against future event delivery changes.
                lock (_lock)
                {
                    if (_state is OrchestratorState.SoftPaused
                        or OrchestratorState.HardPaused
                        or OrchestratorState.Stopped)
                    {
                        tcs.TrySetResult();
                    }
                }
            }

            await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            OnStateChanged -= OnChanged;
            fallbackCts?.Dispose();
        }
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
    /// Atomically resume only if currently soft-paused. No-op otherwise.
    /// Unlike Resume(), this will NOT resume from HardPaused — closing
    /// the TOCTOU window where a health check could transition to HardPaused
    /// between our check and the Resume() call.
    /// </summary>
    public void ResumeIfSoftPaused()
    {
        lock (_lock)
        {
            if (_state != OrchestratorState.SoftPaused)
                return;

            _softPauseRequested = false;

            SetStateLocked(OrchestratorState.Running);
            StartHealthTimerLocked(); // Must restart health timer — matches Resume() behavior
            RaiseLog("Resuming from soft pause.");
            TryStartNextPhaseLocked();
        }
        FlushDeferredEvents();
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
            StopHealthTimerLocked();
            SetStateLocked(OrchestratorState.WaitingForWork);
            RaiseLog("No pending phases. Waiting for new work.");
            return;
        }

        var (feature, phase) = next.Value;

        _backlogService.MarkPhaseStatus(feature.Id, phase.Id, PhaseStatus.InProgress);

        if (feature.Status == FeatureStatus.Queued)
            _backlogService.MarkFeatureStatus(feature.Id, FeatureStatus.InProgress);

        _reviewAttempt = 0;
        _lastReviewCriticalSnippet = null;

        // Ensure health timer is running — idempotent, guards against callers
        // that reach here without explicitly starting it (e.g. AfterPhaseEndedLocked)
        StartHealthTimerLocked();

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
            DangerouslySkipPermissions = true,
            ModelOverride = TeamPrompts.TeamModelId
        };

        session.DevCli = devCli;
        _activeSession = session;

        WireDevEvents(session);

        RaiseActiveTaskChanged(feature.Id);
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

        // Structured events for rich chat rendering (via RaiseEvent for consistent delivery order)
        cli.OnTextBlockStart += () => RaiseDevTextBlockStart();
        cli.OnThinkingDelta += text => RaiseDevThinkingDelta(text);
        cli.OnToolUseStarted += (name, id, input) => RaiseDevToolUseStarted(name, id, input);
        cli.OnToolResult += (name, id, content) => RaiseDevToolResult(name, id, content);

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
            RaiseDevCompleted();
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
            RaiseDevError(error);
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
                session.DevNudgeSent = false;
            }
        };
    }

    // ── Dev completion (called under _lock) ────────────────────────

    private void HandleDevCompletedLocked(DevReviewSession session)
    {
        var devText = session.DevResponse.ToString();

        // Extract developer notes — deferred to run after _lock release
        if (TeamNotesService is { } notesService && _workingDirectory is not null)
        {
            var notes = TeamNotesDetector.ExtractNotes(devText);
            if (notes.Count > 0)
            {
                var projectPath = _workingDirectory;
                var featureId = session.FeatureId;
                var featureTitle = session.FeatureTitle;
                RaiseEvent(() => notesService.AddNotes(projectPath, "developer", featureId, featureTitle, notes));
            }
        }

        // If developer was fixing review issues and dismissed the reviewer, skip re-review
        if (session.CurrentPhase == SessionPhase.FixingIssues
            && ReviewService.DetectReviewDismiss(devText))
        {
            RaiseLog($"Developer dismissed reviewer for [{session.PhaseTitle}] — low quality feedback.");

            var commitHash = _gitService.RunGit("rev-parse --short HEAD", _workingDirectory)?.Trim();
            _backlogService.MarkPhaseStatus(
                session.FeatureId, session.PhaseId, PhaseStatus.Done,
                summary: TruncateSummary(devText) +
                    "\n[Reviewer dismissed by developer — low quality feedback]",
                changedFiles: session.ChangedFiles,
                commitHash: commitHash,
                userActions: ExtractUserActions(devText));

            // Track review dismiss on the feature
            _backlogService.ModifyFeature(session.FeatureId, f => f.ReviewDismissed = true);

            SaveSessionHistory(session);
            RaisePhaseCompleted(session.FeatureId, session.PhaseId, PhaseStatus.Done);

            CleanupSessionLocked();
            AfterPhaseEndedLocked();
            return;
        }

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

        // Record error but keep feature InProgress so remaining phases can run
        _backlogService.ModifyFeature(session.FeatureId, f =>
        {
            f.ErrorSummary = $"Dev failed: {error}";
            f.ErrorDetails = TruncateErrorDetails(session.DevResponse.ToString());
        });

        SaveSessionHistory(session);
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
        reviewer.Configure(_claudeExePath ?? "claude", workDir, TeamPrompts.TeamModelId);
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
            RaiseReviewCompleted();
            lock (_lock)
            {
                if (_activeSession != session) return; // stale callback
                HandleReviewCompletedLocked(session, fullText, verdict);
            }
            FlushDeferredEvents();
        };

        reviewer.OnError += error =>
        {
            RaiseReviewError(error);
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

        // Extract reviewer notes — deferred to run after _lock release
        if (TeamNotesService is { } notesService && _workingDirectory is not null
            && !string.IsNullOrEmpty(reviewText))
        {
            var notes = TeamNotesDetector.ExtractNotes(reviewText);
            if (notes.Count > 0)
            {
                var projectPath = _workingDirectory;
                var featureId = session.FeatureId;
                var featureTitle = session.FeatureTitle;
                RaiseEvent(() => notesService.AddNotes(projectPath, "reviewer", featureId, featureTitle, notes));
            }
        }

        if (verdict == ReviewVerdict.Consensus)
        {
            var devText = session.DevResponse.ToString();
            var commitHash = _gitService.RunGit("rev-parse --short HEAD", _workingDirectory)?.Trim();

            _backlogService.MarkPhaseStatus(
                session.FeatureId, session.PhaseId, PhaseStatus.Done,
                summary: TruncateSummary(devText),
                changedFiles: session.ChangedFiles,
                commitHash: commitHash,
                userActions: ExtractUserActions(devText));

            SaveSessionHistory(session);
            RaiseLog($"Phase [{session.PhaseTitle}] completed with CONSENSUS.");
            RaisePhaseCompleted(session.FeatureId, session.PhaseId, PhaseStatus.Done);

            CleanupSessionLocked();
            AfterPhaseEndedLocked();
            return;
        }

        // ISSUES_FOUND or None
        _reviewAttempt++;

        if (_reviewAttempt >= _maxReviewRetries)
        {
            RaiseLog($"Phase [{session.PhaseTitle}] reached maximum re-review limit ({_reviewAttempt}).");

            _backlogService.MarkPhaseStatus(
                session.FeatureId, session.PhaseId, PhaseStatus.MaxReviewReached,
                errorMessage: $"Maximum re-review reached after {_reviewAttempt} rounds");

            // Record info but keep feature InProgress so remaining phases can run
            _backlogService.ModifyFeature(session.FeatureId, f =>
            {
                f.ErrorSummary = $"Maximum re-review reached after {_reviewAttempt} rounds";
                f.ErrorDetails = TruncateErrorDetails(reviewText);
            });

            SaveSessionHistory(session);
            RaisePhaseCompleted(session.FeatureId, session.PhaseId, PhaseStatus.MaxReviewReached);

            CleanupSessionLocked();
            AfterPhaseEndedLocked();
            return;
        }

        // Loop detection
        var criticalSnippet = ExtractFirstCritical(reviewText);
        if (criticalSnippet is not null && criticalSnippet == _lastReviewCriticalSnippet)
        {
            RaiseLog($"Review loop detected for [{session.PhaseTitle}]. Marking done with warning.");

            var loopDevText = session.DevResponse.ToString();
            _backlogService.MarkPhaseStatus(
                session.FeatureId, session.PhaseId, PhaseStatus.Done,
                summary: TruncateSummary(loopDevText) +
                    "\n[Review loop detected - passed with warning]",
                changedFiles: session.ChangedFiles,
                userActions: ExtractUserActions(loopDevText));

            SaveSessionHistory(session);
            RaisePhaseCompleted(session.FeatureId, session.PhaseId, PhaseStatus.Done);

            CleanupSessionLocked();
            AfterPhaseEndedLocked();
            return;
        }
        _lastReviewCriticalSnippet = criticalSnippet;

        // Send fix prompt to developer
        RaiseLog($"Review round {_reviewAttempt}/{_maxReviewRetries} for [{session.PhaseTitle}]. Sending fixes...");

        session.CurrentPhase = SessionPhase.FixingIssues;
        session.DevResponse.Clear();
        session.ReviewResponse.Clear();

        // Condense review text for later rounds to reduce token usage
        var reviewForFix = CondenseReviewForFix(reviewText, _reviewAttempt);

        var fixPrompt = $"""
            A code reviewer found issues in your work:

            {reviewForFix}

            Please fix all the issues identified above. After fixing, provide a summary of changes.

            Then evaluate the reviewer's feedback quality:
            - If the issues were genuine bugs, security problems, or logic errors → end with `REVIEW_QUALITY: HIGH`
            - If the issues were mostly style preferences, minor suggestions without real impact, or false positives → end with `REVIEW_QUALITY: LOW`
            """;

        // Re-create dev CLI; for rounds > 2, use fresh session to avoid context bloat
        var workDir = session.DevCli?.WorkingDirectory ?? _workingDirectory;
        session.DevCli?.StopSession();

        var devCli = new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = workDir,
            SystemPrompt = TeamPrompts.BuildDeveloperSystemPrompt(
                session.FeatureTitle, session.PhaseTitle,
                session.PhasePlan, session.AcceptanceCriteria),
            DangerouslySkipPermissions = true,
            ModelOverride = TeamPrompts.TeamModelId
        };

        if (_reviewAttempt <= 2 && session.DevSessionId is not null)
            devCli.RestoreSession(session.DevSessionId);
        else if (_reviewAttempt > 2)
            RaiseLog($"Fix round {_reviewAttempt}: using fresh session (context optimization).");
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
        RaiseActiveTaskChanged(null);
    }

    private void CleanupSessionLocked()
    {
        _activeSession?.DevCli?.StopSession();
        _activeSession?.Reviewer?.Stop();
        _activeSession = null;
        RaiseActiveTaskChanged(null);
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

        ClaudeCliService? devCliToNudge = null;
        ReviewService? reviewerToNudge = null;
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

                // 2a. Dev stall: nudge at 360s — capture ref, send outside lock
                if (session.CurrentPhase is SessionPhase.Development or SessionPhase.FixingIssues
                    && session.DevStallSeconds >= _devStallNudgeThresholdSeconds
                    && !session.DevNudgeSent)
                {
                    session.DevNudgeSent = true;
                    devCliToNudge = session.DevCli;
                    RaiseLog($"Dev nudged after {session.DevStallSeconds}s inactivity.");
                }

                // 2b. Dev stall threshold → kill and restart
                if (session.CurrentPhase is SessionPhase.Development or SessionPhase.FixingIssues
                    && session.DevStallSeconds >= _devStallKillThresholdSeconds)
                {
                    RaiseLog($"Dev stalled {session.DevStallSeconds}s. Nudge was sent: {session.DevNudgeSent}. Retry {session.RetryCount}/{_maxSessionRetries}.");
                    RestartDevSessionLocked(session, "stall timeout");
                    return;
                }

                // 3. Review stall: nudge at 60s, kill at 660s (treat as consensus)
                if (session.CurrentPhase == SessionPhase.Review)
                {
                    if (session.ReviewStallSeconds >= _reviewStallKillThresholdSeconds)
                    {
                        RaiseLog($"Reviewer stalled {session.ReviewStallSeconds}s. Treating as consensus.");
                        HandleReviewCompletedLocked(session, "", ReviewVerdict.Consensus);
                        return;
                    }
                    if (session.ReviewStallSeconds >= StallNudgeThresholdSeconds && !session.NudgeSent)
                    {
                        session.NudgeSent = true;
                        reviewerToNudge = session.Reviewer;
                        RaiseLog("Reviewer nudged after 60s inactivity.");
                    }
                }

                FireHealthSnapshotLocked(session);
            }

            // Send nudge messages outside the lock to avoid blocking on stdin pipe.
            // Re-validate captured references: a concurrent tick may have restarted
            // the session (RestartDevSessionLocked), replacing DevCli/Reviewer.
            // Sending to a stale (stopped) CLI could auto-restart an orphan process.
            if (devCliToNudge != null)
            {
                bool stillCurrent;
                lock (_lock) { stillCurrent = _activeSession == session && session.DevCli == devCliToNudge; }
                if (stillCurrent)
                    devCliToNudge.SendMessage("Are you still working? Please continue with the current task.");
            }
            if (reviewerToNudge != null)
            {
                bool stillCurrent;
                lock (_lock) { stillCurrent = _activeSession == session && session.Reviewer == reviewerToNudge; }
                if (stillCurrent)
                    reviewerToNudge.SendNudge();
            }
        }
        finally { FlushDeferredEvents(); }
    }

    private void RestartDevSessionLocked(DevReviewSession session, string reason)
    {
        session.RetryCount++;
        if (session.RetryCount > _maxSessionRetries)
        {
            RaiseLog($"Dev restart limit ({_maxSessionRetries}) exceeded ({reason}). Failing phase.");
            RaiseError($"Phase [{session.PhaseTitle}] failed: {reason} after {_maxSessionRetries} retries");

            _backlogService.MarkPhaseStatus(session.FeatureId, session.PhaseId,
                PhaseStatus.Failed, errorMessage: $"Dev failed: {reason}");

            // Record error but keep feature InProgress so remaining phases can run
            _backlogService.ModifyFeature(session.FeatureId, f =>
            {
                f.ErrorSummary = $"Dev failed: {reason} after {_maxSessionRetries} retries";
                f.ErrorDetails = TruncateErrorDetails(session.DevResponse.ToString());
            });

            SaveSessionHistory(session);
            RaisePhaseCompleted(session.FeatureId, session.PhaseId, PhaseStatus.Failed);

            CleanupSessionLocked();
            AfterPhaseEndedLocked();
            return;
        }

        RaiseLog($"Restarting dev session (attempt {session.RetryCount}/{_maxSessionRetries}, reason: {reason}).");

        var workDir = session.DevCli?.WorkingDirectory ?? _workingDirectory;
        session.DevCli?.StopSession();
        session.DevStallSeconds = 0;
        session.DevNudgeSent = false;

        var devCli = new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = workDir,
            SystemPrompt = TeamPrompts.BuildDeveloperSystemPrompt(
                session.FeatureTitle, session.PhaseTitle,
                session.PhasePlan, session.AcceptanceCriteria),
            DangerouslySkipPermissions = true,
            ModelOverride = TeamPrompts.TeamModelId
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
        var phaseName = session.CurrentPhase.ToString();

        if (session.CurrentPhase is SessionPhase.Development or SessionPhase.FixingIssues)
        {
            var health = session.DevCli is null || !session.DevCli.IsProcessRunning
                ? SessionHealth.Error
                : session.DevStallSeconds >= StallDisplayThresholdSeconds
                    ? SessionHealth.Stalled
                    : SessionHealth.Healthy;

            var detail = session.DevStallSeconds >= StallDisplayThresholdSeconds
                ? $"stalled {session.DevStallSeconds}s" : "";

            var role = session.CurrentPhase == SessionPhase.FixingIssues ? "Dev (fixing)" : "Dev";
            var isFixing = session.CurrentPhase == SessionPhase.FixingIssues;
            items.Add(new SessionHealthInfo(role, health, detail, elapsed,
                idleSeconds: session.DevStallSeconds,
                reviewRound: isFixing ? _reviewAttempt + 1 : 0,
                maxReviewRounds: isFixing ? _maxReviewRetries : 0,
                phaseName: phaseName));
        }

        if (session.CurrentPhase == SessionPhase.Review)
        {
            var health = session.ReviewStallSeconds >= StallNudgeThresholdSeconds
                ? SessionHealth.Stalled
                : SessionHealth.Healthy;

            var detail = session.ReviewStallSeconds >= StallNudgeThresholdSeconds
                ? $"stalled {session.ReviewStallSeconds}s" : "";

            items.Add(new SessionHealthInfo("Review", health, detail, elapsed,
                idleSeconds: session.ReviewStallSeconds,
                reviewRound: _reviewAttempt + 1,
                maxReviewRounds: _maxReviewRetries,
                phaseName: phaseName));
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

    // ── Session history ────────────────────────────────────────────

    private static readonly string SessionHistoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin", "session-history");

    /// <summary>
    /// Prepare session history data under lock, then defer file I/O to after lock release.
    /// Must be called under _lock.
    /// </summary>
    private void SaveSessionHistory(DevReviewSession session)
    {
        // Capture all data under lock (strings are immutable snapshots)
        var featureId = session.FeatureId;
        var featureTitle = session.FeatureTitle;
        var phaseId = session.PhaseId;
        var phaseTitle = session.PhaseTitle;
        var startedAt = session.StartedAt;
        var devText = session.DevResponse.ToString();
        var reviewText = session.ReviewResponse.ToString();
        var timestamp = DateTime.Now;

        // Defer file I/O — runs after _lock is released via FlushDeferredEvents
        _deferredEvents.Add(() => WriteSessionHistoryFile(
            featureId, phaseId, featureTitle, phaseTitle, startedAt, timestamp, devText, reviewText));
    }

    private void WriteSessionHistoryFile(string featureId, string phaseId,
        string featureTitle, string phaseTitle, DateTime startedAt, DateTime endedAt,
        string devText, string reviewText)
    {
        try
        {
            Directory.CreateDirectory(SessionHistoryDir);
            var fileName = $"{featureId}_{phaseId}_{endedAt:yyyyMMdd_HHmmss}.txt";
            var filePath = Path.Combine(SessionHistoryDir, fileName);

            var sb = new StringBuilder();
            sb.AppendLine("=== Session History ===");
            sb.AppendLine($"Feature: {featureTitle}");
            sb.AppendLine($"Phase: {phaseTitle}");
            sb.AppendLine($"Started: {startedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Ended: {endedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            if (devText.Length > 0)
            {
                sb.AppendLine("--- Developer Output ---");
                sb.AppendLine(devText);
                sb.AppendLine();
            }

            if (reviewText.Length > 0)
            {
                sb.AppendLine("--- Review Output ---");
                sb.AppendLine(reviewText);
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString());

            _backlogService.ModifyFeature(featureId, f =>
            {
                if (!f.SessionHistoryPaths.Contains(filePath))
                    f.SessionHistoryPaths.Add(filePath);
            });
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Failed to save session history: {ex.Message}");
        }
    }

    // ── Utilities ──────────────────────────────────────────────────

    private const int MaxErrorDetailsLength = 10_000;

    private static string TruncateErrorDetails(string text)
    {
        if (text.Length <= MaxErrorDetailsLength) return text;
        return text[..MaxErrorDetailsLength] + "\n\n[truncated — see session history for full output]";
    }

    private static string TruncateSummary(string text)
    {
        if (text.Length <= 500) return text;

        var sepIdx = text.LastIndexOf("---", StringComparison.Ordinal);
        if (sepIdx > 0 && text.Length - sepIdx < 1000)
            return text[(sepIdx + 3)..].Trim();

        return text[^500..];
    }

    /// <summary>
    /// Extracts userActions JSON array from dev output, if present.
    /// Developer is instructed to output: {"userActions": ["action1", "action2"]}
    /// </summary>
    private static List<string>? ExtractUserActions(string devOutput)
    {
        var json = JsonBlockExtractor.Extract(devOutput, "userActions");
        if (json is null) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("userActions", out var arr)
                && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var actions = new List<string>();
                foreach (var item in arr.EnumerateArray())
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        actions.Add(text);
                }
                return actions.Count > 0 ? actions : null;
            }
        }
        catch { /* ignore parse errors */ }

        return null;
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

    /// <summary>
    /// For later review rounds (3+), extract only actionable items (CRITICAL/WARNING/BUG)
    /// to reduce token usage when the developer starts a fresh session.
    /// </summary>
    private static string CondenseReviewForFix(string reviewText, int attempt)
    {
        if (attempt <= 2 || reviewText.Length < 2000)
            return reviewText;

        var lines = reviewText.Split('\n');
        var sb = new StringBuilder();
        var capturing = false;
        var inCodeBlock = false;
        string? lastSectionHeader = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Track code blocks — keep them if we're capturing (they contain fix context)
            if (trimmed.StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                if (capturing) { sb.AppendLine(line); continue; }
                continue;
            }

            if (inCodeBlock) { if (capturing) sb.AppendLine(line); continue; }

            // Section boundary: markdown header (##) or verdict/summary lines end the current capture
            var isSectionBoundary = trimmed.StartsWith('#')
                || trimmed.StartsWith("VERDICT", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("USER_NOTE", StringComparison.OrdinalIgnoreCase);

            if (isSectionBoundary)
            {
                if (capturing) capturing = false;
                if (trimmed.StartsWith('#')) lastSectionHeader = line;
            }

            var wroteAsHeader = false;
            if (trimmed.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
                || BugWordRegex.IsMatch(trimmed)
                || trimmed.Contains("**Severity**", StringComparison.OrdinalIgnoreCase))
            {
                // Retroactively include the section header for context
                if (!capturing && lastSectionHeader != null)
                {
                    sb.AppendLine(lastSectionHeader);
                    wroteAsHeader = (lastSectionHeader == line);
                    lastSectionHeader = null;
                }
                capturing = true;
            }

            if (capturing && !wroteAsHeader)
                sb.AppendLine(line);
        }

        // Unclosed code block — condensation unreliable, fall back to truncation
        if (inCodeBlock)
            return TruncateAtLine(reviewText, 3000, "[...truncated for context optimization]");

        var condensed = sb.ToString().Trim();
        if (condensed.Length < 100)
            return TruncateAtLine(reviewText, 3000, "[...truncated for context optimization]");

        return TruncateAtLine(condensed, 3000, "[...truncated]");
    }

    private static string TruncateAtLine(string text, int maxLen, string suffix)
    {
        if (text.Length <= maxLen) return text;
        var cutoff = text.LastIndexOf('\n', maxLen);
        if (cutoff < 100) cutoff = maxLen;
        return text[..cutoff] + "\n" + suffix;
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
    public bool DevNudgeSent;
    public int RetryCount;
}

public enum SessionPhase
{
    Development,
    Review,
    FixingIssues
}
