using System.IO;
using System.Text.Json;
using System.Windows;
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

        _ = Task.Run(() => _projectRegistry.RegisterProject(projectRoot, _gitService));
    }

    public bool HandleEscape()
    {
        if (MessageQueue.Count > 0)
        {
            var last = MessageQueue[^1];
            MessageQueue.RemoveAt(MessageQueue.Count - 1);
            InputText = last.Text;
            return true;
        }

        if (IsProcessing)
        {
            CancelProcessing();
            return true;
        }

        return false;
    }

    private void CancelProcessing()
    {
        _cliService.Cancel();
        IsProcessing = false;
        StatusText = "Cancelled";
        UpdateCta(CtaState.WaitingForUser);

        if (_currentAssistantMessage is not null)
        {
            _currentAssistantMessage.IsStreaming = false;
            _currentAssistantMessage.IsThinking = false;
            _currentAssistantMessage = null;
        }
    }

    public void AddTaskOutput(string taskName, string output)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var msg = new MessageViewModel(MessageRole.System, $"Task \"{taskName}\" completed")
            {
                TaskOutputText = output
            };
            Messages.Add(msg);
        });
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

        var viewer = new DiffViewerWindow(filePath, diff)
        {
            Owner = Application.Current.MainWindow
        };
        viewer.Show();
    }

    private static void ShowImagePreview(FileAttachment att)
    {
        var mainWindow = Application.Current.MainWindow;
        if (mainWindow is not null)
            ClaudeCodeWin.MainWindow.ShowImagePreviewWindow(mainWindow, att.FilePath, att.FileName);
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
            CtaState.Processing => "Claude is working. Send a message to queue it, or press Escape to cancel.",
            CtaState.WaitingForUser => "Claude is waiting for your response",
            CtaState.AnswerQuestion => "Answer the question above",
            CtaState.ConfirmOperation => "Confirm the operation above",
            _ => ""
        };
        OnPropertyChanged(nameof(HasCta));
    }
}
