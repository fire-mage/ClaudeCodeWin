using System.IO;
using System.Windows;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    private void SelectFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Project Folder"
        };

        if (dialog.ShowDialog() == true)
            SetWorkingDirectory(dialog.FolderName);
    }

    public void SetWorkingDirectory(string folder)
    {
        // Stop existing process when switching projects
        _cliService.StopSession();

        _cliService.WorkingDirectory = folder;
        _settings.WorkingDirectory = folder;

        // Add to recent folders (move to top if already exists)
        RecentFolders.Remove(folder);
        RecentFolders.Insert(0, folder);
        _settings.RecentFolders.Remove(folder);
        _settings.RecentFolders.Insert(0, folder);

        // Keep max 10 recent folders
        while (RecentFolders.Count > 10)
        {
            RecentFolders.RemoveAt(RecentFolders.Count - 1);
            _settings.RecentFolders.RemoveAt(_settings.RecentFolders.Count - 1);
        }

        _settingsService.Save(_settings);
        ShowWelcome = false;
        ProjectPath = folder;
        _registeredProjectRoots.Add(Path.GetFullPath(folder));
        RefreshGitStatus();

        // Register project in registry
        _ = Task.Run(() => _projectRegistry.RegisterProject(folder, _gitService));

        // Rebuild file index in background
        _ = Task.Run(() => RefreshAutocompleteIndex());

        // Generate context snapshot in background
        if (_settings.ContextSnapshotEnabled)
            _contextSnapshotService.StartGenerationInBackground([folder]);

        // Try to restore saved session, otherwise start fresh
        if (_settings.SavedSessions.TryGetValue(folder, out var saved)
            && DateTime.Now - saved.CreatedAt < TimeSpan.FromHours(24))
        {
            // Restore previous session
            if (IsProcessing)
                CancelProcessing();
            Messages.Clear();
            ModelName = "";
            StatusText = "";

            _cliService.RestoreSession(saved.SessionId);

            var folderName = Path.GetFileName(folder) ?? folder;
            var resumeTime = saved.CreatedAt.ToString("HH:mm");
            Messages.Add(new MessageViewModel(MessageRole.System,
                $"Project loaded: {folderName}\nResumed session from {resumeTime}. Type your message to continue."));
            UpdateCta(CtaState.WaitingForUser);
        }
        else
        {
            // Start fresh session
            StartNewSession();

            var folderName = Path.GetFileName(folder) ?? folder;
            Messages.Add(new MessageViewModel(MessageRole.System,
                $"Project loaded: {folderName}\nType your message below to start working. Enter sends, Shift+Enter for newline."));
        }
    }

    private void StartNewSession()
    {
        if (IsProcessing)
            CancelProcessing();

        // Save current chat before clearing
        SaveChatHistory();
        _currentChatId = null;

        Messages.Clear();
        MessageQueue.Clear();
        ChangedFiles.Clear();
        _cliService.ClearFileSnapshots();
        _cliService.ResetSession();
        ModelName = "";
        StatusText = "";
        ContextUsageText = "";
        TodoProgressText = "";
        _contextWarningShown = false;
        _contextWindowSize = 0;

        // Reset finalize actions state
        ShowTaskSuggestion = false;
        ShowFinalizeActionsLabel = false;
        HasCompletedTask = false;
        SuggestedTasks.Clear();
        StopTaskSuggestionTimer();

        // Clear saved session for current project
        if (!string.IsNullOrEmpty(WorkingDirectory)
            && _settings.SavedSessions.Remove(WorkingDirectory))
        {
            _settingsService.Save(_settings);
        }

        UpdateCta(CtaState.Ready);
    }

    private void SwitchToOpus()
    {
        _cliService.ModelOverride = "opus";
        StartNewSession();
        Messages.Add(new MessageViewModel(MessageRole.System, "Switching to Opus. Next message will use claude-opus."));
    }

    private void ExpandContext()
    {
        // Get current model base name (strip existing [1m] suffix if any)
        var currentModel = _modelName;
        if (string.IsNullOrEmpty(currentModel))
            currentModel = "sonnet";

        if (currentModel.Contains("[1m]"))
        {
            // Already in 1M mode — switch back to standard
            _cliService.ModelOverride = currentModel.Replace("[1m]", "");
            StartNewSession();
            Messages.Add(new MessageViewModel(MessageRole.System, "Switched back to standard context window (200K)."));
        }
        else
        {
            // Expand to 1M
            var baseModel = currentModel switch
            {
                var m when m.Contains("opus") => "opus",
                var m when m.Contains("haiku") => "haiku",
                _ => "sonnet"
            };
            _cliService.ModelOverride = $"{baseModel}[1m]";
            StartNewSession();
            _contextWarningShown = false;
            Messages.Add(new MessageViewModel(MessageRole.System, "Expanding context window to 1M tokens. Starting new session."));
        }
    }

    private void RefreshGitStatus()
    {
        var (branch, dirtyCount, unpushedCount) = _gitService.GetStatus(WorkingDirectory);
        if (branch is null)
        {
            GitStatusText = "no git";
            return;
        }

        if (dirtyCount == 0 && unpushedCount == 0)
        {
            GitStatusText = $"git: {branch}, clean";
            return;
        }

        var parts = new List<string> { $"git: {branch}" };

        if (dirtyCount > 0)
            parts.Add($"{dirtyCount} file(s) unstaged");

        if (unpushedCount > 0)
            parts.Add($"{unpushedCount} unpushed");

        GitStatusText = string.Join(", ", parts);
    }

    private void RefreshAutocompleteIndex()
    {
        var names = _projectRegistry.Projects.Select(p => p.Name).ToList();
        _fileIndexService.SetProjectNames(names);
    }

    private void SaveChatHistory()
    {
        // Only save if there are user/assistant messages
        var chatMessages = Messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant)
            .ToList();
        if (chatMessages.Count == 0) return;

        var entry = new ChatHistoryEntry
        {
            Id = _currentChatId ?? Guid.NewGuid().ToString(),
            ProjectPath = WorkingDirectory,
            SessionId = _cliService.SessionId,
            Messages = chatMessages.Select(m => new ChatMessage
            {
                Role = m.Role,
                Text = m.Text,
                Timestamp = m.Timestamp,
                ToolUses = m.ToolUses.Select(t => new ToolUseInfo
                {
                    ToolName = t.ToolName,
                    ToolUseId = t.ToolUseId,
                    Input = t.Input,
                    Output = t.Output,
                    Summary = t.Summary
                }).ToList()
            }).ToList()
        };

        // Title = first ~80 chars of first user message
        var firstUser = chatMessages.FirstOrDefault(m => m.Role == MessageRole.User);
        entry.Title = firstUser is not null
            ? (firstUser.Text.Length > 80 ? firstUser.Text[..80] + "..." : firstUser.Text)
            : "Untitled";

        if (_currentChatId is null)
        {
            entry.CreatedAt = chatMessages[0].Timestamp;
            _currentChatId = entry.Id;
        }

        try { _chatHistoryService.Save(entry); } catch { }
    }

    public void LoadChatFromHistory(ChatHistoryEntry entry)
    {
        if (IsProcessing)
            CancelProcessing();

        Messages.Clear();
        MessageQueue.Clear();

        _currentChatId = entry.Id;

        // Restore session if available
        if (!string.IsNullOrEmpty(entry.SessionId))
            _cliService.RestoreSession(entry.SessionId);
        else
            _cliService.ResetSession();

        // Restore messages
        foreach (var msg in entry.Messages)
        {
            var vm = new MessageViewModel(msg.Role, msg.Text);
            foreach (var tool in msg.ToolUses)
                vm.ToolUses.Add(new ToolUseViewModel(tool.ToolName, tool.ToolUseId, tool.Input));
            Messages.Add(vm);
        }

        // Switch to project if different
        if (!string.IsNullOrEmpty(entry.ProjectPath) && entry.ProjectPath != WorkingDirectory)
        {
            _cliService.WorkingDirectory = entry.ProjectPath;
            _settings.WorkingDirectory = entry.ProjectPath;
            _settingsService.Save(_settings);
            ProjectPath = entry.ProjectPath;
            RefreshGitStatus();
            _ = Task.Run(() => RefreshAutocompleteIndex());
        }

        ShowWelcome = false;
        ModelName = "";
        StatusText = "";

        Messages.Add(new MessageViewModel(MessageRole.System,
            $"Loaded chat from history. {(entry.SessionId is not null ? "Session restored — you can continue." : "No session to restore.")}"));
        UpdateCta(CtaState.WaitingForUser);
    }

    public void ShowProjectPickerIfNeeded()
    {
        var projects = _projectRegistry.GetMostRecentProjects(20);
        if (projects.Count < 2) return;

        // Filter out nested sub-projects (keep the topmost project root)
        var sorted = projects.OrderBy(p => p.Path.Length).ToList();
        var roots = new List<ProjectInfo>();
        foreach (var p in sorted)
        {
            var normalizedPath = p.Path.TrimEnd('\\', '/') + "\\";
            var isNested = roots.Any(r =>
                normalizedPath.StartsWith(r.Path.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase));
            if (!isNested)
                roots.Add(p);
        }

        // Re-sort by LastOpened descending and take top 10
        var filtered = roots.OrderByDescending(p => p.LastOpened).Take(10).ToList();
        if (filtered.Count < 2) return;

        PickerProjects.Clear();
        foreach (var p in filtered)
            PickerProjects.Add(p);

        ShowProjectPicker = true;
    }
}
