using System.IO;
using System.Windows;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Sets the working directory on startup (before the CLI process starts).
    /// Unlike SetWorkingDirectory, this doesn't stop/start sessions or show messages.
    /// Initializes all tab-visible properties (TabTitle, GitStatus, etc.) so restored tabs
    /// show the correct project name immediately.
    /// </summary>
    public void SetWorkingDirectoryOnStartup(string folder)
    {
        _cliService.WorkingDirectory = folder;
        ProjectPath = folder;
        _registeredProjectRoots.Add(Path.GetFullPath(folder));
        LockProject?.Invoke(folder);
        RefreshGitStatus();
        StartGitRefreshTimer();
        UpdateExplorerRoot();
        _ = Task.Run(() => RefreshAutocompleteIndex());
        _ = Task.Run(() => _projectRegistry.RegisterProject(folder, _gitService));
        if (_settings.ContextSnapshotEnabled)
            _contextSnapshotService.StartGenerationInBackground([folder]);
    }

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
        // Check if this project is already open in another tab
        if (IsProjectLockedByOtherTab?.Invoke(folder) == true)
        {
            Messages.Add(new MessageViewModel(MessageRole.System,
                $"Project \"{Path.GetFileName(folder)}\" is already open in another tab."));
            return;
        }

        // Unlock previous project before switching
        UnlockCurrentProject?.Invoke();

        // Stop existing process when switching projects
        _cliService.StopSession();

        _cliService.WorkingDirectory = folder;
        _settings.WorkingDirectory = folder;

        // Add to recent folders (move to top if already exists)
        RecentFolders.Remove(folder);
        RecentFolders.Insert(0, folder);
        _settings.RecentFolders.Remove(folder);
        _settings.RecentFolders.Insert(0, folder);

        // Keep max 10 recent folders — trim each list independently to avoid
        // IndexOutOfRange if they get out of sync
        while (RecentFolders.Count > 10)
            RecentFolders.RemoveAt(RecentFolders.Count - 1);
        while (_settings.RecentFolders.Count > 10)
            _settings.RecentFolders.RemoveAt(_settings.RecentFolders.Count - 1);

        _settingsService.Save(_settings);
        ProjectPath = folder;
        _registeredProjectRoots.Add(Path.GetFullPath(folder));

        // Lock this project in the tab system
        LockProject?.Invoke(folder);
        RefreshGitStatus();
        StartGitRefreshTimer();
        UpdateExplorerRoot();

        // Register project in registry
        _ = Task.Run(() => _projectRegistry.RegisterProject(folder, _gitService));

        // Rebuild file index in background
        _ = Task.Run(() => RefreshAutocompleteIndex());

        // Clear stale snapshots from previous project, then generate for new project only
        if (_settings.ContextSnapshotEnabled)
        {
            _contextSnapshotService.InvalidateAll();
            _contextSnapshotService.StartGenerationInBackground([folder]);
        }

        // Always start fresh session when switching projects
        // (Session restore at startup is handled by the constructor + Welcome screen "Continue Chat")
        StartNewSession(); // sets _needsPreambleInjection = true

        var folderName = Path.GetFileName(folder) ?? folder;
        Messages.Add(new MessageViewModel(MessageRole.System,
            $"Project loaded: {folderName}\nType your message below to start working. Enter sends, Shift+Enter for newline."));
    }

    private void StartNewSession()
    {
        if (IsProcessing)
            CancelProcessing();

        // Save current chat before clearing
        SaveChatHistory();
        _currentChatId = null;

        _messageAssembler.ClearMessages();
        MessageQueue.Clear();
        ChangedFiles.Clear();
        ClearBackgroundTasks();
        _cliService.ClearFileSnapshots();
        // FIX (WARNING #3): ResetSessionAsync stops the process and nulls _sessionId.
        // Null _sessionId synchronously to prevent BuildArguments from using a stale
        // session ID if SendMessage is called before the async stop completes.
        // The async stop itself is safe to fire-and-forget (has internal error handling).
        _cliService.ResetSessionSync();
        ModelName = "";
        StatusText = "";
        ReviewStatusText = "";
        ContextUsageText = "";
        ContextPctText = "";
        TodoProgressText = "";
        _contextWarningShown = false;
        _contextWindowSize = 0;
        _needsPreambleInjection = true;
        _apiKeyExpiryChecked = false;
        _pendingQuestionAnswers.Clear();
        _pendingQuestionMessages.Clear();
        _pendingQuestionCount = 0;
        _pendingControlRequestId = null;
        _pendingControlToolUseId = null;
        _pendingQuestionInput = null;

        // Reset finalize actions state
        FinalizeActions.ShowTaskSuggestion = false;
        FinalizeActions.ShowFinalizeActionsLabel = false;
        FinalizeActions.HasCompletedTask = false;
        FinalizeActions.SuggestedTasks.Clear();
        FinalizeActions.StopTaskSuggestionTimer();
        StopNudgeTimer();

        // Clear saved session for current project
        if (!string.IsNullOrEmpty(WorkingDirectory)
            && _settings.SavedSessions.Remove(WorkingDirectory))
        {
            _settingsService.Save(_settings);
        }

        // Regenerate context snapshot fresh for current project (as if app just started)
        if (_settings.ContextSnapshotEnabled && !string.IsNullOrEmpty(WorkingDirectory))
        {
            _contextSnapshotService.InvalidateAll();
            _contextSnapshotService.StartGenerationInBackground([WorkingDirectory]);
        }

        UpdateCta(CtaState.Ready);
    }

    private void SwitchToOpus()
    {
        _cliService.ModelOverride = "opus";
        StartNewSession();
        Messages.Add(new MessageViewModel(MessageRole.System, "Switching to Opus. Next message will use claude-opus."));
    }


    private void StartGitRefreshTimer()
    {
        _gitRefreshTimer?.Stop();
        _gitRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _gitRefreshTimer.Tick += async (_, _) =>
        {
            try
            {
                var dir = WorkingDirectory;
                if (string.IsNullOrEmpty(dir)) return;
                var result = await Task.Run(() => _gitService.GetStatus(dir));
                if (WorkingDirectory == dir)
                    ApplyGitStatus(result);
            }
            catch { /* async void — swallow to prevent app crash */ }
        };
        _gitRefreshTimer.Start();
    }

    /// <summary>
    /// Detect changed files via git status as a fallback for tools that don't report file changes
    /// (e.g. Bash with sed/node). Only adds files that are NEW compared to the pre-turn snapshot,
    /// so only files changed by Claude in this turn are tracked (not pre-existing dirty files).
    /// Must be awaited so ChangedFiles is populated before SaveChatHistory / OnTurnCompleted.
    /// </summary>
    private async Task DetectChangedFilesFromGitAsync()
    {
        if (string.IsNullOrEmpty(WorkingDirectory)) return;

        var baseline = _preTurnDirtyFiles;
        var files = await Task.Run(() => _gitService.GetChangedFiles(WorkingDirectory));
        foreach (var file in files)
        {
            if (!baseline.Contains(file) && !ChangedFiles.Any(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
                ChangedFiles.Add(file);
        }
    }

    private void RefreshGitStatus()
    {
        ApplyGitStatus(_gitService.GetStatus(WorkingDirectory));
    }

    private void ApplyGitStatus((string? branch, int dirtyCount, int unpushedCount) result)
    {
        var (branch, dirtyCount, unpushedCount) = result;
        if (branch is null)
        {
            HasGitRepo = false;
            GitDirtyText = "no git";
            GitStatusText = "no git";
            return;
        }

        HasGitRepo = true;

        if (dirtyCount == 0 && unpushedCount == 0)
        {
            GitDirtyText = "clean";
            GitStatusText = "clean";
            return;
        }

        var parts = new List<string>();

        if (dirtyCount > 0)
            parts.Add($"{dirtyCount} file(s) unstaged");

        if (unpushedCount > 0)
            parts.Add($"{unpushedCount} unpushed");

        var dirtyText = string.Join(", ", parts);
        GitDirtyText = dirtyText;
        GitStatusText = dirtyText;
    }

    private void RefreshAutocompleteIndex()
    {
        var names = _projectRegistry.Projects.Select(p => p.Name).ToList();
        _fileIndexService.SetProjectNames(names);

        // Build file index for @-mention autocomplete
        if (!string.IsNullOrEmpty(WorkingDirectory))
            _fileIndexService.BuildFileIndex(WorkingDirectory);
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

        _messageAssembler.ClearMessages();
        MessageQueue.Clear();

        _currentChatId = entry.Id;

        // Restore session if available
        if (!string.IsNullOrEmpty(entry.SessionId))
            _cliService.RestoreSession(entry.SessionId);
        else
            _cliService.ResetSessionAsync().ContinueWith(t =>
                Services.DiagnosticLogger.Log("RESET_SESSION_ERROR", t.Exception?.InnerException?.Message ?? "unknown"),
                TaskContinuationOptions.OnlyOnFaulted);
        _needsPreambleInjection = true;
        ResetTaskOutputSentFlags();

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
            UpdateExplorerRoot();
            _ = Task.Run(() => RefreshAutocompleteIndex());
        }

        ModelName = "";
        StatusText = "";

        Messages.Add(new MessageViewModel(MessageRole.System,
            $"Loaded chat from history. {(entry.SessionId is not null ? "Session restored — you can continue." : "No session to restore.")}"));
        UpdateCta(CtaState.WaitingForUser);
    }

    public void StartGeneralChat()
    {
        _cliService.StopSession();
        _cliService.WorkingDirectory = null;
        _settings.WorkingDirectory = null;
        _settingsService.Save(_settings);

        _gitRefreshTimer?.Stop();
        _gitRefreshTimer = null;

        ProjectPath = "";
        NewSessionCommand.Execute(null);
    }
}
