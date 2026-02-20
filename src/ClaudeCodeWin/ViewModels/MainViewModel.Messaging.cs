using System.Windows;
using ClaudeCodeWin.Models;

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

        ChangedFiles.Clear();
        _cliService.ClearFileSnapshots();
        InputText = string.Empty;
        IsProcessing = true;
        StatusText = "Processing...";
        UpdateCta(CtaState.Processing);

        // Auto-inject system instruction and context snapshot on first message of a new session
        var finalPrompt = text;
        if (_cliService.SessionId is null)
        {
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
        Application.Current.Dispatcher.InvokeAsync(() =>
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
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_currentAssistantMessage is not null)
            {
                if (_isFirstDelta)
                {
                    _isFirstDelta = false;
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
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_currentAssistantMessage is not null)
            {
                _hadToolsSinceLastText = true;

                if (_isFirstDelta)
                {
                    _isFirstDelta = false;
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
        Application.Current.Dispatcher.InvokeAsync(() =>
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

    private void HandleCompleted(ResultData result)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_currentAssistantMessage is not null)
            {
                _currentAssistantMessage.IsStreaming = false;
                _currentAssistantMessage.IsThinking = false;
            }

            _currentAssistantMessage = null;
            _hadToolsSinceLastText = false;
            IsProcessing = false;
            StatusText = "Ready";
            UpdateCta(CtaState.WaitingForUser);

            if (!string.IsNullOrEmpty(result.Model))
                ModelName = result.Model;

            // Track context usage
            if (result.ContextWindow > 0)
                _contextWindowSize = result.ContextWindow;
            if (_contextWindowSize > 0 && result.InputTokens > 0)
            {
                var totalTokens = result.InputTokens + result.OutputTokens;
                var pct = (int)(totalTokens * 100.0 / _contextWindowSize);
                ContextUsageText = $"Ctx: {pct}%";

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
            }
        });
    }

    private void HandleFileChanged(string filePath)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!ChangedFiles.Contains(filePath))
                ChangedFiles.Add(filePath);
        });
    }

    private void HandleError(string error)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
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
