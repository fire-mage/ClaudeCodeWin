using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    private async Task SendMessageAsync()
    {
        var (text, inlineAttachments, contentParts) = BuildComposerContent();
        if (string.IsNullOrEmpty(text))
            return;

        // Bar attachments only for queue/recall (inline markers are already in text).
        List<FileAttachment>? barAttachments = Attachments.Count > 0 ? [.. Attachments] : null;

        // Merge bar + inline attachments for the actual CLI send.
        // Both need to be passed as real file attachments so the API receives image data.
        List<FileAttachment>? allAttachments = barAttachments;
        if (inlineAttachments != null)
            allAttachments = [.. (allAttachments ?? []), .. inlineAttachments];

        // If Claude is busy, queue the message (including all attachments — bar + inline).
        // Inline images must be included so the CLI can send actual image data on auto-send.
        // Note: during the review-fix cycle, IsProcessing is true but IsReviewInProgress
        // is false (cleared when verdict arrived). User messages are intentionally queued
        // here (not blocked) — they will be sent after the fix completes and before the
        // next auto-review triggers.
        if (IsProcessing)
        {
            MessageQueue.Add(new QueuedMessage(text, allAttachments));
            ClearComposer();
            return;
        }

        // Block input during code review — don't queue, just discard
        if (IsReviewInProgress)
        {
            Messages.Add(new MessageViewModel(MessageRole.System,
                "Cannot send messages during review. Press Escape to cancel review."));
            return;
        }

        // User manually typed — reset ExitPlanMode loop counter
        _exitPlanModeAutoCount = 0;

        // If there's a pending control request (AskUserQuestion / ExitPlanMode),
        // treat the typed message as the user's custom answer.
        // Use only plain text from TextBlocks — inline image markers would pollute the answer.
        if (_pendingControlRequestId is not null)
        {
            var answerText = string.Join("", ComposerBlocks.OfType<TextComposerBlock>().Select(t => t.Text)).Trim();
            if (string.IsNullOrEmpty(answerText))
            {
                Messages.Add(new MessageViewModel(MessageRole.System, "Please type a text answer."));
                return;
            }
            ClearComposer();
            HandleControlAnswer(answerText);
            return;
        }

        // Mark the start of a new task (user-initiated, not queued clarification or review fix)
        _currentTaskStartIndex = Messages.Count; // Will point to the user message about to be added
        _reviewCycleCompleted = false; // Allow review for the new task
        ChangedFiles.Clear(); // Clear only on user-initiated messages (not queued or review-fix)
        ClearComposer();

        await SendDirectAsync(text, allAttachments, contentParts: contentParts);
    }

    private async Task SendDirectAsync(string text, List<FileAttachment>? attachments,
        string? participantLabel = null, List<MessageContentPart>? contentParts = null)
    {
        // Cancel any active review if this is a real user message (not an auto-review fix prompt)
        if (_reviewService?.IsActive == true && participantLabel is null)
        {
            CancelReview();
            Messages.Add(new MessageViewModel(MessageRole.System, "Review cancelled (user message)."));
        }

        var userMsg = new MessageViewModel(MessageRole.User, text);
        if (participantLabel is not null)
            userMsg.ReviewerLabel = participantLabel;
        if (attachments is not null)
            userMsg.Attachments = [.. attachments];
        if (contentParts is not null)
            userMsg.ContentParts = contentParts;
        Messages.Add(userMsg);

        _lastSentText = text;
        _lastSentAttachments = attachments;
        _hasResponseStarted = false;

        EffectiveProjectName = "";
        _cliService.ClearFileSnapshots();
        _activeSendGeneration = ++_sendGeneration;
        IsProcessing = true;
        StatusText = "Processing...";
        StartNudgeTimer();
        UpdateCta(CtaState.Processing);

        // Inject preamble whenever context may have been lost
        // (new session, resumed session, after context compaction, chat history load)
        var finalPrompt = text;
        if (_needsPreambleInjection)
        {
            _needsPreambleInjection = false;

            var preamble = SystemInstruction;

            // Inject required developer KB articles
            var devKbSection = _devKbService?.BuildRequiredArticlesSection();
            if (!string.IsNullOrEmpty(devKbSection))
                preamble += devKbSection;

            if (_settings.ContextSnapshotEnabled)
            {
                // Wait for background snapshot generation (max 10s)
                await _contextSnapshotService.WaitForGenerationAsync(10000);

                // Inject snapshot for current project only (cache is reset on project switch / new session)
                var snapshotPaths = !string.IsNullOrEmpty(WorkingDirectory)
                    ? new List<string> { WorkingDirectory }
                    : _projectRegistry.GetMostRecentProjects(1).Select(p => p.Path).ToList();
                var (combined, snapshotCount) = _contextSnapshotService.GetCombinedSnapshot(snapshotPaths);
                if (!string.IsNullOrEmpty(combined))
                {
                    preamble += $"\n\n<context-snapshot>\n{combined}\n</context-snapshot>";
                    Messages.Add(new MessageViewModel(MessageRole.System,
                        $"Context snapshot injected ({snapshotCount} projects)"));
                }
            }

            // Inject project registry
            var registrySummary = _projectRegistry.BuildRegistrySummary();
            if (!string.IsNullOrEmpty(registrySummary))
                preamble += $"\n\n<project-registry>\n{registrySummary}\n</project-registry>";

            // Inject SSH access info
            var sshInfo = BuildSshInfo();
            if (!string.IsNullOrEmpty(sshInfo))
                preamble += $"\n\n<ssh-access>\n{sshInfo}\n</ssh-access>";

            // Inject expired/expiring API key warnings into prompt
            var apiKeyWarnings = new List<string>();
            foreach (var key in _settings.ApiKeys)
            {
                var (days, isExpired, _) = key.GetExpiryStatus();
                if (isExpired)
                    apiKeyWarnings.Add($"{key.ServiceName} (expired {-days}d ago)");
            }
            if (apiKeyWarnings.Count > 0)
                preamble += $"\n\n<expired-api-keys>The following API keys are expired and should NOT be used: {string.Join(", ", apiKeyWarnings)}. Ask the user to update them in Settings > API Keys.</expired-api-keys>";

            finalPrompt = $"{preamble}\n\n{text}";

            // Check for expiring/expired API keys (UI notification)
            CheckApiKeyExpiry();
        }

        _messageAssembler.BeginAssistantMessage();

        // Send via persistent process (starts process if needed)
        await Task.Run(() => _cliService.SendMessage(finalPrompt, attachments));
    }

    private void HandleTextBlockStart()
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;
            ResetNudgeActivity();
            _messageAssembler.HandleTextBlockStart();
        });
    }

    private void HandleTextDelta(string text)
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;
            ResetNudgeActivity();
            _messageAssembler.HandleTextDelta(text);
            _hasResponseStarted = _messageAssembler.CurrentMessage is not null;
        });
    }

    private void HandleThinkingDelta(string text)
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;
            ResetNudgeActivity();
            _messageAssembler.HandleThinkingDelta(text);
        });
    }

    private void HandleToolUseStarted(string toolName, string toolUseId, string input)
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;
            ResetNudgeActivity();
            _messageAssembler.HandleToolUseStarted(toolName, toolUseId, input);
            _hasResponseStarted = _messageAssembler.CurrentMessage is not null;

            // Business logic that stays in MainViewModel
            if (toolName == "TodoWrite")
                UpdateTodoProgress(input);
            TryRegisterProjectFromToolUse(toolName, input);
            UpdateEffectiveProject(toolName, input);
            TryTrackBackgroundTask(toolName, toolUseId, input);
        });
    }

    private void HandleToolResult(string toolName, string toolUseId, string content)
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;
            ResetNudgeActivity();
            _messageAssembler.HandleToolResult(toolName, toolUseId, content);
            TryUpdateBackgroundTask(toolName, toolUseId, content);
        });
    }

    private void HandleMessageStarted(string model, int inputTokens, int cacheReadTokens, int cacheCreationTokens)
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;
            ResetNudgeActivity();
            // Show model name immediately when the first API call starts
            if (!string.IsNullOrEmpty(model) && string.IsNullOrEmpty(ModelName))
                ModelName = model;

            // Update context usage in real-time during processing
            if (_contextWindowSize > 0 && inputTokens > 0)
            {
                var totalInput = inputTokens + cacheReadTokens + cacheCreationTokens;
                var pct = (int)(totalInput * 100.0 / _contextWindowSize);
                ContextPctText = $"{pct}%";
            }
        });
    }

    private void HandleCompleted(ResultData result)
    {
        RunOnUI(() =>
        {
            // Skip stale callbacks from a cancelled CLI process
            if (_activeSendGeneration != _sendGeneration) return;

            // Detect ```team-task blocks BEFORE HandleCompleted (which calls ExtractCompletionSummary)
            // (summary extraction removes text after ---, which could contain task blocks)
            List<FeatureProposalDetector.FeatureProposal>? teamProposals = null;
            var currentMsg = _messageAssembler.CurrentMessage;
            if (currentMsg is not null && !string.IsNullOrEmpty(WorkingDirectory))
            {
                var (cleaned, proposals) = FeatureProposalDetector.Extract(currentMsg.Text);
                if (proposals.Count > 0)
                {
                    currentMsg.Text = cleaned;
                    teamProposals = proposals;
                }
            }

            _messageAssembler.HandleCompleted();

            // Add detected features to backlog off the UI thread
            if (teamProposals is not null)
            {
                var wd = WorkingDirectory!;
                var total = teamProposals.Count;
                _ = Task.Run(() =>
                {
                    var succeeded = 0;
                    foreach (var p in teamProposals)
                    {
                        try
                        {
                            var feature = _backlogService.AddFeature(wd, p.RawIdea);
                            if (p.Priority != 100)
                                _backlogService.ModifyFeature(feature.Id, f => f.Priority = p.Priority);
                            _plannerService.StartPlanning(feature);
                            succeeded++;
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLogger.Log("TEAM_TASK_ERROR",
                                $"Failed to add task: {ex.Message}");
                        }
                    }
                    return succeeded;
                }).ContinueWith(t => RunOnUI(() =>
                {
                    var ok = t.IsFaulted ? 0 : t.Result;
                    if (ok > 0) Team.Refresh();
                    var msg = ok == total
                        ? $"{ok} task(s) sent to Team pipeline"
                        : ok > 0
                            ? $"{ok}/{total} task(s) sent to Team pipeline ({total - ok} failed)"
                            : $"Failed to send tasks to Team";
                    Messages.Add(new MessageViewModel(MessageRole.System, msg));
                }), TaskScheduler.Default);
            }

            IsProcessing = false;
            StopNudgeTimer();
            _messageAssembler.ClearAllThinking();
            StatusText = "";
            UpdateCta(CtaState.WaitingForUser);

            if (!string.IsNullOrEmpty(result.Model))
                ModelName = result.Model;

            // Track context usage
            if (result.ContextWindow > 0)
                _contextWindowSize = result.ContextWindow;

            // Use per-call usage from the last message_start event (= actual current conversation size).
            // Falls back to aggregated result.usage if message_start data is unavailable (all zeros).
            var lastCallInput = result.LastCallInputTokens + result.LastCallCacheReadTokens + result.LastCallCacheCreationTokens;
            var lastCallTotal = lastCallInput + result.LastCallOutputTokens;
            var usePerCall = lastCallInput > 0;

            // Aggregated total (for logging / fallback)
            var aggInput = result.InputTokens + result.CacheReadTokens + result.CacheCreationTokens;
            var aggTotal = aggInput + result.OutputTokens;

            var totalTokens = usePerCall ? lastCallTotal : aggTotal;
            var totalInput = usePerCall ? lastCallInput : aggInput;

            if (_contextWindowSize > 0 && totalInput > 0)
            {
                var pct = (int)(totalTokens * 100.0 / _contextWindowSize);

                // Safety: if still using aggregated fallback and pct > 100%, cap display
                // (aggregated values can exceed context window due to multi-call turns)
                if (!usePerCall && pct > 100)
                    pct = Math.Min(pct, 99); // indicate near-full without misleading >100%

                ContextUsageText = $"Ctx: {pct}%";
                ContextPctText = $"{pct}%";

                DiagnosticLogger.Log("CTX",
                    $"source={( usePerCall ? "per-call" : "aggregated" )} " +
                    $"perCall: input={result.LastCallInputTokens:N0} cache_read={result.LastCallCacheReadTokens:N0} " +
                    $"cache_create={result.LastCallCacheCreationTokens:N0} output={result.LastCallOutputTokens:N0} " +
                    $"agg: input={result.InputTokens:N0} cache_read={result.CacheReadTokens:N0} " +
                    $"cache_create={result.CacheCreationTokens:N0} output={result.OutputTokens:N0} " +
                    $"used={totalTokens:N0} window={_contextWindowSize:N0} pct={pct}%");

                // Compaction detection: significant Ctx% drop (>20pp) between turns
                if (_previousCtxPercent > 0 && _previousCtxPercent - pct > 20)
                {
                    var msg = $"Context compacted: {_previousCtxPercent}% \u2192 {pct}% " +
                              $"({_previousInputTokens:N0} \u2192 {totalInput:N0} input tokens)";
                    Messages.Add(new MessageViewModel(MessageRole.System, msg));
                    DiagnosticLogger.Log("COMPACTION_DETECTED", msg);
                    _contextWarningShown = false;
                    _needsPreambleInjection = true;
                    ResetTaskOutputSentFlags();
                }

                _previousInputTokens = totalInput;
                _previousCtxPercent = pct;

                if (pct >= 80 && !_contextWarningShown)
                {
                    _contextWarningShown = true;
                    Messages.Add(new MessageViewModel(MessageRole.System,
                        $"Context is {pct}% full ({totalTokens:N0}/{_contextWindowSize:N0} tokens). Consider starting a new session."));
                }
            }

            // Save session for persistence
            if (!string.IsNullOrEmpty(result.SessionId) && !string.IsNullOrEmpty(WorkingDirectory))
            {
                _settings.SavedSessions[WorkingDirectory] = new SavedSession
                {
                    SessionId = result.SessionId,
                    CreatedAt = DateTime.Now
                };
                _settingsService.Save(_settings);
            }

            RefreshGitStatus();
            SaveChatHistory();

            _notificationService.NotifyIfInactive();

            // Show notification dot on this tab if it's not currently active
            if (!IsActiveTab)
                HasNotification = true;

            // Auto-send next queued message
            if (MessageQueue.Count > 0)
            {
                var next = MessageQueue[0];
                MessageQueue.RemoveAt(0);
                _ = SendDirectAsync(next.Text, next.Attachments);
            }
            else
            {
                // Normal turn completion — reset ExitPlanMode loop counter
                _exitPlanModeAutoCount = 0;

                // Auto-review or task suggestion popup
                OnTurnCompleted();

                // Resume team after conflict pause (only when no review is pending)
                if (_teamPausedForConflict && !IsReviewInProgress)
                    ResumeTeamAfterConflict();
            }
        });
    }

    private bool DetectCompletionMarker()
    {
        // Find the last non-reviewer assistant message
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Role != MessageRole.Assistant) continue;

            // Skip reviewer messages — they contain VERDICT, not completion markers
            if (Messages[i].IsReviewerMessage) continue;

            // If summary was already extracted, it contains the marker
            if (Messages[i].HasCompletionSummary)
            {
                DiagnosticLogger.Log("COMPLETION_MARKER", $"found via HasCompletionSummary at msg[{i}]");
                return true;
            }

            var text = Messages[i].Text;
            if (string.IsNullOrEmpty(text))
            {
                DiagnosticLogger.Log("COMPLETION_MARKER", $"msg[{i}] has empty text, skipping");
                continue;
            }

            // Check last 500 chars for completion markers
            var tail = text.Length > 500 ? text[^500..] : text;
            var lower = tail.ToLowerInvariant();

            foreach (var marker in MessageViewModel.CompletionMarkers)
            {
                if (lower.Contains(marker))
                {
                    DiagnosticLogger.Log("COMPLETION_MARKER", $"found '{marker}' in tail of msg[{i}] (len={text.Length})");
                    return true;
                }
            }

            DiagnosticLogger.Log("COMPLETION_MARKER",
                $"no marker in msg[{i}] (len={text.Length}), tail(last100): {(tail.Length > 100 ? tail[^100..] : tail)}");
            break; // Only check the last non-reviewer assistant message
        }

        DiagnosticLogger.Log("COMPLETION_MARKER", "not found in any assistant message");
        return false;
    }

    private void TryShowTaskSuggestion()
    {
        if (_taskRunnerService is null || string.IsNullOrEmpty(WorkingDirectory))
            return;

        // Check conditions: changed files + completion marker.
        // When review cycle completed, the cycle itself is the completion signal —
        // skip marker check (fix responses end with REVIEW_QUALITY: LOW, not "Done").
        if (ChangedFiles.Count == 0)
            return;
        if (!_reviewCycleCompleted && !DetectCompletionMarker())
            return;

        // Determine effective projects from changed file paths (not just WorkingDirectory)
        var effectiveProjects = GetEffectiveProjectPaths();

        // Check if dismissed for ALL effective projects
        if (effectiveProjects.Count > 0 && effectiveProjects.All(p =>
                _settings.TaskSuggestionDismissedProjects.Contains(
                    p.NormalizePath(), StringComparer.OrdinalIgnoreCase)))
            return;

        // Build suggestion list across all effective projects
        var suggestions = new List<TaskSuggestionItem>();
        var addedTaskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasGit = false;

        foreach (var projectPath in effectiveProjects)
        {
            // Get project-specific deploy tasks
            var projectTasks = _taskRunnerService.GetTasksForProject(projectPath);
            var deployTasks = projectTasks
                .Where(t => t.Command.Contains("deploy", StringComparison.OrdinalIgnoreCase)
                            || t.Command.Contains("publish", StringComparison.OrdinalIgnoreCase))
                .Take(3);

            foreach (var dt in deployTasks)
            {
                if (addedTaskNames.Add(dt.Name))
                    suggestions.Add(new TaskSuggestionItem { Label = dt.Name, Task = dt });
            }

            // Check if project has git (walk up to find .git in parent dirs, e.g. monorepo)
            if (!hasGit && IsInsideGitRepo(projectPath))
                hasGit = true;
        }

        if (hasGit)
            suggestions.Add(new TaskSuggestionItem { Label = "Commit & Push", IsCommit = true });

        if (suggestions.Count == 0)
            return;

        FinalizeActions.SuggestedTasks.Clear();
        foreach (var s in suggestions)
            FinalizeActions.SuggestedTasks.Add(s);

        FinalizeActions.ProjectName = effectiveProjects.Count == 1
            ? Path.GetFileName(effectiveProjects[0])
            : string.Join(", ", effectiveProjects.Select(Path.GetFileName));
        FinalizeActions.HasCompletedTask = true;
        FinalizeActions.ShowFinalizeActionsLabel = false;
        FinalizeActions.ShowTaskSuggestion = true;
        FinalizeActions.StartAutoCollapseTimer();
    }

    /// <summary>
    /// Determines which project(s) were actually modified by looking at ChangedFiles paths
    /// and matching them against the project registry.
    /// Falls back to WorkingDirectory if no registry match is found.
    /// </summary>
    private List<string> GetEffectiveProjectPaths()
    {
        var projectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in ChangedFiles)
        {
            var match = FindProjectForFile(filePath);
            if (match is not null)
                projectPaths.Add(match);
        }

        // Fall back to working directory if no project matched any changed file
        if (projectPaths.Count == 0 && !string.IsNullOrEmpty(WorkingDirectory))
            projectPaths.Add(WorkingDirectory);

        return projectPaths.ToList();
    }

    /// <summary>
    /// Finds the most specific project from the registry that contains the given file path.
    /// Returns null if no project matches.
    /// </summary>
    private string? FindProjectForFile(string filePath)
    {
        string? bestMatch = null;
        var bestLength = 0;

        foreach (var project in _projectRegistry.Projects)
        {
            var projectPath = project.Path.NormalizePath();
            if (filePath.IsSubPathOf(projectPath) && projectPath.Length > bestLength)
            {
                bestMatch = project.Path;
                bestLength = projectPath.Length;
            }
        }

        return bestMatch;
    }

    private void HandleFileChanged(string filePath)
    {
        RunOnUI(() =>
        {
            if (!ChangedFiles.Contains(filePath))
                ChangedFiles.Add(filePath);
        });
    }

    private void HandleError(string error)
    {
        RunOnUI(() =>
        {
            // Skip stale callbacks from a cancelled CLI process
            if (_activeSendGeneration != _sendGeneration) return;

            _messageAssembler.HandleError(error);

            IsProcessing = false;
            StopNudgeTimer();
            _messageAssembler.ClearAllThinking();
            StatusText = "Error";
            UpdateCta(CtaState.WaitingForUser);

            // Warn user if queued messages are stranded (HandleError doesn't auto-drain)
            if (MessageQueue.Count > 0)
                Messages.Add(new MessageViewModel(MessageRole.System,
                    $"{MessageQueue.Count} queued message(s) not sent. Click a message to send or return to input."));

            if (_teamPausedForConflict)
                ResumeTeamAfterConflict();

            _notificationService.NotifyIfInactive();
        });
    }

    /// <summary>
    /// Walks up the directory tree from the given path to find a .git directory.
    /// Handles cases where the project registry entry is a subfolder (e.g. src/ClaudeCodeWin)
    /// but .git lives at the repo root (e.g. ClaudeCodeWin/).
    /// </summary>
    private static bool IsInsideGitRepo(string path)
    {
        var dir = path;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return true;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir) break;
            dir = parent;
        }
        return false;
    }

    private void ResumeTeamAfterConflict()
    {
        if (!_teamPausedForConflict) return; // Already resumed — avoid resuming unrelated soft pause
        var wasSoftPaused = _orchestratorService?.State == Services.OrchestratorState.SoftPaused;
        _teamPausedForConflict = false;
        ConflictBannerText = "";
        OnPropertyChanged(nameof(IsConflictBannerVisible));
        // Cancel safety timer — normal cleanup happened
        _conflictBannerClearTimer?.Stop();
        _conflictBannerClearTimer = null;
        // Clear pending IDs so HandleConflictPauseAsync catch block won't send duplicate response
        _pendingConflictRequestId = null;
        _pendingConflictToolUseId = null;
        _conflictPauseCts?.Cancel();
        _conflictPauseCts?.Dispose();
        _conflictPauseCts = null;
        _orchestratorService?.ResumeIfSoftPaused();
        if (wasSoftPaused)
            Messages.Add(new MessageViewModel(MessageRole.System, "Team resumed."));
    }

    // ─── Composer helpers ───

    /// <summary>
    /// Build the final prompt text from composer blocks. Inline images become [Screenshot:] markers
    /// interleaved with text, and their FileAttachment objects are collected separately so the CLI
    /// can send the actual image data to the API (not just the path string).
    /// Also builds ContentParts list to preserve interleaved text/image order for chat display.
    /// </summary>
    private (string text, List<FileAttachment>? inlineAttachments, List<MessageContentPart>? contentParts) BuildComposerContent()
    {
        var sb = new StringBuilder();
        List<FileAttachment>? inlineAtts = null;
        List<MessageContentPart>? contentParts = null;
        bool hasImages = ComposerBlocks.Any(b => b is ImageComposerBlock);

        foreach (var block in ComposerBlocks)
        {
            switch (block)
            {
                case TextComposerBlock tb:
                    sb.Append(tb.Text);
                    if (hasImages && !string.IsNullOrEmpty(tb.Text))
                        (contentParts ??= []).Add(MessageContentPart.CreateText(tb.Text));
                    break;
                case ImageComposerBlock ib:
                    (inlineAtts ??= []).Add(ib.Attachment);
                    (contentParts ??= []).Add(MessageContentPart.CreateImage(ib.Attachment));
                    sb.AppendLine();
                    sb.AppendLine(ib.Attachment.IsScreenshot
                        ? $"[Screenshot: {ib.FilePath}]"
                        : $"[File: {ib.FilePath}]");
                    break;
            }
        }
        return (sb.ToString().Trim(), inlineAtts, contentParts);
    }

    /// <summary>Reset composer blocks only (preserves attachment bar).</summary>
    public void ClearComposerText()
    {
        ComposerBlocks.Clear();
        ComposerBlocks.Add(new TextComposerBlock());
        OnPropertyChanged(nameof(IsComposerEmpty));
    }

    /// <summary>Full reset: composer blocks + attachment bar.</summary>
    public void ClearComposer()
    {
        ClearComposerText();
        Attachments.Clear();
    }

}
