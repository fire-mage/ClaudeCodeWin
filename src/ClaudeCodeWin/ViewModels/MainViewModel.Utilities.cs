using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    private void TryRegisterProjectFromToolUse(string toolName, string inputJson)
    {
        string? filePath = null;
        try
        {
            if (string.IsNullOrEmpty(inputJson) || !inputJson.StartsWith('{'))
                return;

            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;

            filePath = toolName switch
            {
                "Read" or "Write" or "Edit" =>
                    root.TryGetProperty("file_path", out var fp) ? fp.GetString() : null,
                "NotebookEdit" =>
                    root.TryGetProperty("notebook_path", out var np) ? np.GetString() : null,
                "Glob" or "Grep" =>
                    root.TryGetProperty("path", out var p) ? p.GetString() : null,
                _ => null
            };
        }
        catch { return; }

        if (string.IsNullOrEmpty(filePath) || !Path.IsPathRooted(filePath))
            return;

        var projectRoot = ProjectRegistryService.DetectProjectRoot(filePath);
        if (projectRoot is null || !_registeredProjectRoots.Add(projectRoot))
            return;

        // Don't auto-register parent directories of the current working directory
        if (!string.IsNullOrEmpty(WorkingDirectory))
        {
            var currentDir = Path.GetFullPath(WorkingDirectory);
            var detectedDir = Path.GetFullPath(projectRoot);
            if (currentDir.IsSubPathOf(detectedDir))
                return;
        }

        _ = Task.Run(() => _projectRegistry.RegisterProject(projectRoot, _gitService));
    }

    private void UpdateEffectiveProject(string toolName, string inputJson)
    {
        string? filePath = null;
        try
        {
            if (string.IsNullOrEmpty(inputJson) || !inputJson.StartsWith('{'))
                return;

            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;

            filePath = toolName switch
            {
                "Read" or "Write" or "Edit" =>
                    root.TryGetProperty("file_path", out var fp) ? fp.GetString() : null,
                "NotebookEdit" =>
                    root.TryGetProperty("notebook_path", out var np) ? np.GetString() : null,
                "Glob" or "Grep" =>
                    root.TryGetProperty("path", out var p) ? p.GetString() : null,
                _ => null
            };
        }
        catch { return; }

        if (string.IsNullOrEmpty(filePath) || !Path.IsPathRooted(filePath))
            return;

        var projectPath = FindProjectForFile(filePath);
        if (projectPath is null)
            return;

        var projectName = Path.GetFileName(projectPath.TrimEnd('\\', '/'));
        var workingName = Path.GetFileName(WorkingDirectory?.TrimEnd('\\', '/') ?? "");

        EffectiveProjectName = string.Equals(projectName, workingName, StringComparison.OrdinalIgnoreCase)
            ? ""
            : projectName;
    }

    /// <summary>
    /// Recalls the last sent message back to the input box if Claude hasn't started responding yet.
    /// Returns true if the message was recalled.
    /// </summary>
    public bool RecallLastMessage()
    {
        if (!IsProcessing || _hasResponseStarted || _lastSentText is null)
            return false;

        // Cancel the pending request
        _cliService.Cancel();
        IsProcessing = false;
        StatusText = "";
        UpdateCta(CtaState.WaitingForUser);

        // Remove the assistant "thinking" bubble
        if (_messageAssembler.CurrentMessage is not null)
        {
            var thinkingMsg = _messageAssembler.CurrentMessage;
            Messages.Remove(thinkingMsg);
            // FIX (WARNING #2): Dispose individually removed messages to stop leaked timers
            thinkingMsg.Dispose();
            _messageAssembler.Reset();
        }

        // Remove the user message that was just sent
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Role == MessageRole.User)
            {
                // FIX (WARNING #2): Dispose individually removed messages to stop leaked timers
                Messages[i].Dispose();
                Messages.RemoveAt(i);
                break;
            }
        }

        // Restore text and attachments to the input box
        // Strip inline markers — images are restored as bar attachments
        InputText = StripInlineMarkers(_lastSentText);
        if (_lastSentAttachments is not null)
        {
            foreach (var att in _lastSentAttachments)
                AddAttachment(att);
        }

        _lastSentText = null;
        _lastSentAttachments = null;
        return true;
    }

    public bool HandleEscape()
    {
        if (MessageQueue.Count > 0)
        {
            var last = MessageQueue[^1];
            MessageQueue.RemoveAt(MessageQueue.Count - 1);
            InputText = StripInlineMarkers(last.Text);
            if (last.Attachments != null)
                foreach (var att in last.Attachments)
                    AddAttachment(att);
            return true;
        }

        if (IsProcessing)
        {
            // Try full recall first (if Claude hasn't started responding yet)
            if (RecallLastMessage())
                return true;

            // Claude already started streaming — cancel but preserve the user's input
            var textToRestore = _lastSentText;
            var attachmentsToRestore = _lastSentAttachments;
            _lastSentText = null;
            _lastSentAttachments = null;
            CancelProcessing();
            if (textToRestore != null)
                InputText = StripInlineMarkers(textToRestore);
            if (attachmentsToRestore != null)
                foreach (var att in attachmentsToRestore)
                    AddAttachment(att);
            return true;
        }

        if (_reviewService is not null)
        {
            CancelReview();
            StatusText = "Review cancelled";
            UpdateCta(CtaState.WaitingForUser);
            Messages.Add(new MessageViewModel(MessageRole.System, "Review cancelled by user."));
            if (_teamPausedForConflict)
                ResumeTeamAfterConflict();
            return true;
        }

        return false;
    }

    private void CancelProcessing()
    {
        // Invalidate stale HandleCompleted/HandleError callbacks that may already
        // be queued on the dispatcher from the about-to-be-killed CLI process.
        _sendGeneration++;

        _cliService.Cancel();
        CancelReview();
        IsProcessing = false;
        StopNudgeTimer();
        StatusText = "Cancelled";
        UpdateCta(CtaState.WaitingForUser);

        if (_messageAssembler.CurrentMessage is not null)
        {
            _messageAssembler.CurrentMessage.IsStreaming = false;
            _messageAssembler.CurrentMessage.IsThinking = false;
            _messageAssembler.Reset();
        }

        _messageAssembler.ClearAllThinking();

        if (_teamPausedForConflict)
            ResumeTeamAfterConflict();
    }

    /// <summary>
    /// Defensive cleanup: clear IsThinking on ALL messages in the collection.
    /// Prevents stale "thinking" indicators when _currentAssistantMessage was reassigned
    /// (e.g. by HandleTextBlockStart) and the old message wasn't properly cleared.
    /// </summary>
    private void ClearAllThinking() => _messageAssembler.ClearAllThinking();


    public void AddTaskOutput(string taskName, string output)
    {
        RunOnUI(() =>
        {
            // Store full output for sending to Claude, truncated for UI display
            var displayOutput = output.Length > 5000
                ? output[..5000] + "\n... (truncated)"
                : output;

            var msg = new MessageViewModel(MessageRole.System, $"Task \"{taskName}\" completed")
            {
                TaskOutputFull = output,
                TaskOutputText = displayOutput
            };
            Messages.Add(msg);
        });
    }

    /// <summary>
    /// Resets task output "sent" flags so the user can re-send after context loss.
    /// Called when preamble re-injection is triggered (compaction, session restore, etc.).
    /// </summary>
    private void ResetTaskOutputSentFlags()
    {
        foreach (var msg in Messages)
        {
            if (msg.HasTaskOutput && msg.IsTaskOutputSent)
                msg.IsTaskOutputSent = false;
        }
    }

    public void AddAttachment(FileAttachment attachment)
    {
        if (Attachments.All(a => a.FilePath != attachment.FilePath))
            Attachments.Add(attachment);
    }

    private void ShowFileDiff(string filePath)
    {
        var oldContent = _cliService.GetFileSnapshot(filePath);

        string? newContent;
        try
        {
            newContent = File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        }
        catch
        {
            newContent = null;
        }

        if (oldContent is null && newContent is null)
        {
            MessageBox.Show($"Cannot read file:\n{filePath}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var diff = DiffService.ComputeDiff(oldContent, newContent);

        var viewer = new DiffViewerWindow(filePath, diff);
        // BUG FIX: Application.Current.MainWindow can be null during shutdown
        if (Application.Current?.MainWindow is { } mainWin)
            viewer.Owner = mainWin;
        viewer.Show();
    }

    private static void ShowImagePreview(FileAttachment att)
    {
        // BUG FIX: Application.Current can be null during shutdown
        var mainWindow = Application.Current?.MainWindow;
        if (mainWindow is not null)
            Infrastructure.ImagePreviewHelper.ShowPreviewWindow(mainWindow, att.FilePath, att.FileName);
    }

    private void UpdateTodoProgress(string inputJson)
    {
        try
        {
            if (string.IsNullOrEmpty(inputJson)) return;
            using var doc = JsonDocument.Parse(inputJson);
            if (!doc.RootElement.TryGetProperty("todos", out var todos)
                || todos.ValueKind != JsonValueKind.Array)
                return;

            int total = 0, done = 0;
            foreach (var todo in todos.EnumerateArray())
            {
                total++;
                var status = todo.TryGetProperty("status", out var s) ? s.GetString() : "";
                if (status == "completed") done++;
            }

            TodoProgressText = total > 0 ? $"Tasks: {done}/{total}" : "";
        }
        catch (JsonException) { }
    }

    private string? BuildSshInfo()
    {
        var hasKey = !string.IsNullOrEmpty(_settings.SshKeyPath);
        var sshPassword = SettingsService.Unprotect(_settings.SshMasterPasswordProtected ?? "");
        var hasPassword = !string.IsNullOrEmpty(sshPassword);
        var hasServers = _settings.Servers.Count > 0;

        if (!hasKey && !hasPassword && !hasServers)
            return null;

        var lines = new List<string> { "## SSH Access" };

        if (hasKey)
        {
            lines.Add($"- Claude's SSH private key path: `{_settings.SshKeyPath}`");
            lines.Add($"- When deploying or connecting via SSH, use this key with `-i \"{_settings.SshKeyPath}\"` flag");
        }

        if (hasPassword)
        {
            lines.Add($"- SSH master password for servers that don't accept key auth: `{sshPassword}`");
            lines.Add("- Use `sshpass -p '{password}' ssh ...` when key-based auth is not available");
        }

        if (hasServers)
        {
            lines.Add("");
            lines.Add("### Known servers");
            foreach (var s in _settings.Servers)
            {
                var desc = !string.IsNullOrEmpty(s.Description) ? $" — {s.Description}" : "";
                var projects = s.Projects.Count > 0 ? $" (Projects: {string.Join(", ", s.Projects)})" : "";
                lines.Add($"- **{s.Name}**: `{s.User}@{s.Host}:{s.Port}`{desc}{projects}");
            }
        }

        return string.Join("\n", lines);
    }

    private void UpdateCta(CtaState state)
    {
        _ctaState = state;
        CtaText = state switch
        {
            CtaState.Welcome => "",
            CtaState.Ready => "Start a conversation with Claude",
            CtaState.Processing => "Claude is working. Press \u2191 to recall, Escape to cancel, or send to queue.",
            CtaState.WaitingForUser => "Claude is waiting for your response",
            CtaState.AnswerQuestion => "Answer the question above",
            CtaState.ConfirmOperation => "Confirm the operation above",
            CtaState.Reviewing => "Review in progress. Wait for completion or press Escape to cancel.",
            _ => ""
        };
        OnPropertyChanged(nameof(HasCta));
    }

    /// <summary>
    /// Strip [Screenshot: path] and [File: path] markers from text.
    /// Used when restoring text to the composer — inline images are already
    /// restored as bar attachments, so the markers are redundant.
    /// </summary>
    private static string StripInlineMarkers(string text)
    {
        if (!text.Contains("[Screenshot:") && !text.Contains("[File:"))
            return text;
        return Regex.Replace(text, @"\r?\n?\[(?:Screenshot|File): [^\]]+\]\r?\n?", "\n").Trim();
    }

    private void CheckApiKeyExpiry()
    {
        if (_apiKeyExpiryChecked || _settings.ApiKeys.Count == 0) return;
        _apiKeyExpiryChecked = true;

        var warnings = new List<string>();
        foreach (var key in _settings.ApiKeys)
        {
            var (days, isExpired, isWarning) = key.GetExpiryStatus();
            if (isExpired)
                warnings.Add($"{key.ServiceName} API key expired {-days} days ago");
            else if (isWarning)
                warnings.Add(days == 0
                    ? $"{key.ServiceName} API key expires today"
                    : $"{key.ServiceName} API key expires in {days} days");
        }

        if (warnings.Count > 0)
        {
            var msg = "API key warning: " + string.Join("; ", warnings)
                + ". Go to Settings > API Keys to update.";
            Messages.Add(new MessageViewModel(MessageRole.System, msg));
        }
    }
}
