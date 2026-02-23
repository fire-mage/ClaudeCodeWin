using System.IO;
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
        var text = InputText?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        // If Claude is busy, queue the message (including any attachments)
        if (IsProcessing)
        {
            List<FileAttachment>? queuedAttachments = Attachments.Count > 0 ? [.. Attachments] : null;
            MessageQueue.Add(new QueuedMessage(text, queuedAttachments));
            InputText = string.Empty;
            if (queuedAttachments is not null)
                Attachments.Clear();
            return;
        }

        // User manually typed — reset ExitPlanMode loop counter
        _exitPlanModeAutoCount = 0;

        // If there's a pending control request (AskUserQuestion / ExitPlanMode),
        // treat the typed message as the user's custom answer
        if (_pendingControlRequestId is not null)
        {
            InputText = string.Empty;
            HandleControlAnswer(text);
            return;
        }

        await SendDirectAsync(text, Attachments.Count > 0 ? [.. Attachments] : null);
    }

    private async Task SendDirectAsync(string text, List<FileAttachment>? attachments)
    {
        var userMsg = new MessageViewModel(MessageRole.User, text);
        if (attachments is not null)
            userMsg.Attachments = [.. attachments];
        Messages.Add(userMsg);

        if (attachments is not null)
            Attachments.Clear();

        _lastSentText = text;
        _lastSentAttachments = attachments;
        _hasResponseStarted = false;

        ChangedFiles.Clear();
        _cliService.ClearFileSnapshots();
        InputText = string.Empty;
        IsProcessing = true;
        StatusText = "Processing...";
        UpdateCta(CtaState.Processing);

        // Inject preamble whenever context may have been lost
        // (new session, resumed session, after context compaction, chat history load)
        var finalPrompt = text;
        if (_needsPreambleInjection)
        {
            _needsPreambleInjection = false;

            var preamble = SystemInstruction;

            if (_settings.ContextSnapshotEnabled)
            {
                // Wait for background snapshot generation (max 10s)
                await _contextSnapshotService.WaitForGenerationAsync(10000);

                // Inject snapshots for recent projects from registry
                var recentPaths = _projectRegistry.GetMostRecentProjects(5).Select(p => p.Path).ToList();
                var (combined, snapshotCount) = _contextSnapshotService.GetCombinedSnapshot(recentPaths);
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

            finalPrompt = $"{preamble}\n\n{text}";
        }

        _currentAssistantMessage = new MessageViewModel(MessageRole.Assistant) { IsStreaming = true, IsThinking = true };
        _isFirstDelta = true;
        Messages.Add(_currentAssistantMessage);

        // Send via persistent process (starts process if needed)
        await Task.Run(() => _cliService.SendMessage(finalPrompt, attachments));
    }

    private void HandleTextBlockStart()
    {
        RunOnUI(() =>
        {
            if (_currentAssistantMessage is null) return;

            // If tools were used since the last text block, start a new message bubble
            if (_hadToolsSinceLastText)
            {
                _currentAssistantMessage.IsStreaming = false;
                _currentAssistantMessage = new MessageViewModel(MessageRole.Assistant) { IsStreaming = true };
                Messages.Add(_currentAssistantMessage);
                _hadToolsSinceLastText = false;
                _isFirstDelta = true;
            }
        });
    }

    private void HandleTextDelta(string text)
    {
        RunOnUI(() =>
        {
            if (_currentAssistantMessage is not null)
            {
                if (_isFirstDelta)
                {
                    _isFirstDelta = false;
                    _hasResponseStarted = true;
                    _currentAssistantMessage.IsThinking = false;
                    _currentAssistantMessage.Text = text;
                }
                else
                {
                    _currentAssistantMessage.Text += text;
                }
            }
        });
    }

    private void HandleToolUseStarted(string toolName, string toolUseId, string input)
    {
        RunOnUI(() =>
        {
            if (_currentAssistantMessage is not null)
            {
                _hadToolsSinceLastText = true;

                if (_isFirstDelta)
                {
                    _isFirstDelta = false;
                    _hasResponseStarted = true;
                    _currentAssistantMessage.IsThinking = false;
                }

                // Check if this tool use already exists (update with complete input)
                var existing = _currentAssistantMessage.ToolUses
                    .FirstOrDefault(t => t.ToolUseId == toolUseId && !string.IsNullOrEmpty(toolUseId));

                if (existing is not null)
                {
                    // Update existing with complete input (from content_block_stop)
                    existing.UpdateInput(input);
                }
                else
                {
                    _currentAssistantMessage.ToolUses.Add(new ToolUseViewModel(toolName, toolUseId, input));
                }

                // Update TodoWrite progress in status bar
                if (toolName == "TodoWrite")
                    UpdateTodoProgress(input);
            }

            TryRegisterProjectFromToolUse(toolName, input);
        });
    }

    private void HandleToolResult(string toolName, string toolUseId, string content)
    {
        RunOnUI(() =>
        {
            if (_currentAssistantMessage is null) return;

            // Find the tool use by ID and set its result
            var tool = _currentAssistantMessage.ToolUses
                .FirstOrDefault(t => t.ToolUseId == toolUseId)
                ?? _currentAssistantMessage.ToolUses.LastOrDefault(t => t.ToolName == toolName);

            if (tool is not null)
            {
                // Truncate large results for display
                tool.ResultContent = content.Length > 5000
                    ? content[..5000] + $"\n\n... ({content.Length:N0} chars total)"
                    : content;
            }
        });
    }

    private void HandleMessageStarted(string model, int inputTokens, int cacheReadTokens, int cacheCreationTokens)
    {
        RunOnUI(() =>
        {
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
            if (_currentAssistantMessage is not null)
            {
                _currentAssistantMessage.IsStreaming = false;
                _currentAssistantMessage.IsThinking = false;
                _currentAssistantMessage.ExtractCompletionSummary();
            }

            _currentAssistantMessage = null;
            _hadToolsSinceLastText = false;
            IsProcessing = false;
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
                        $"Context is {pct}% full ({totalTokens:N0}/{_contextWindowSize:N0} tokens). Consider starting a new session or expanding to 1M."));
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
            _notificationService.NotifyIfInactive();
            SaveChatHistory();

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

                // Task suggestion popup: show when task appears completed
                TryShowTaskSuggestion();
            }
        });
    }

    private bool DetectCompletionMarker()
    {
        // Find the last assistant message
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Role != MessageRole.Assistant) continue;

            // If summary was already extracted, it contains the marker
            if (Messages[i].HasCompletionSummary)
                return true;

            var text = Messages[i].Text;
            if (string.IsNullOrEmpty(text)) continue;

            // Check last 500 chars for completion markers
            var tail = text.Length > 500 ? text[^500..] : text;
            var lower = tail.ToLowerInvariant();

            foreach (var marker in MessageViewModel.CompletionMarkers)
            {
                if (lower.Contains(marker))
                    return true;
            }

            break; // Only check the last assistant message
        }

        return false;
    }

    private void TryShowTaskSuggestion()
    {
        if (_taskRunnerService is null || string.IsNullOrEmpty(WorkingDirectory))
            return;

        // Check conditions: changed files + completion marker
        if (ChangedFiles.Count == 0 || !DetectCompletionMarker())
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

            // Check if project has git
            if (!hasGit && Directory.Exists(Path.Combine(projectPath, ".git")))
                hasGit = true;
        }

        if (hasGit)
            suggestions.Add(new TaskSuggestionItem { Label = "Commit & Push", IsCommit = true });

        if (suggestions.Count == 0)
            return;

        FinalizeActions.SuggestedTasks.Clear();
        foreach (var s in suggestions)
            FinalizeActions.SuggestedTasks.Add(s);

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
            if (_currentAssistantMessage is not null)
            {
                _currentAssistantMessage.IsStreaming = false;
                _currentAssistantMessage.IsThinking = false;
                if (string.IsNullOrEmpty(_currentAssistantMessage.Text))
                    _currentAssistantMessage.Text = $"Error: {error}";
            }

            _currentAssistantMessage = null;
            IsProcessing = false;
            StatusText = "Error";
            UpdateCta(CtaState.WaitingForUser);

            _notificationService.NotifyIfInactive();
        });
    }
}
