using System.IO;
using System.Windows;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Sets the working directory on startup (before the CLI process starts).
    /// Unlike SetWorkingDirectory, this doesn't stop/start sessions or show messages.
    /// </summary>
    public void SetWorkingDirectoryOnStartup(string folder)
    {
        _cliService.WorkingDirectory = folder;
        ProjectPath = folder;
        _registeredProjectRoots.TryAdd(Path.GetFullPath(folder), 0);
        LockProject?.Invoke(folder);
        RefreshGitStatus();
        StartGitRefreshTimer();
        UpdateExplorerRoot();
        _ = Task.Run(() => RefreshAutocompleteIndex());
        _ = Task.Run(() =>
        {
            _projectRegistry.RegisterProject(folder, _gitService);
        }).ContinueWith(_ =>
            _onboardingService?.TryStartOnboarding(folder),
            TaskScheduler.FromCurrentSynchronizationContext());
        if (_settings.ContextSnapshotEnabled)
            _contextSnapshotService.StartGenerationInBackground(GetAllProjectPaths());
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

    public void SetWorkingDirectory(string folder) => SetWorkingDirectoryCore(folder, showMessage: true);

    /// <summary>
    /// Internal variant that skips the "Project loaded" message (used by SetActiveWorkspace
    /// which shows its own workspace-level message).
    /// </summary>
    private void SetWorkingDirectoryCore(string folder, bool showMessage)
    {
        if (string.IsNullOrEmpty(folder)) return;

        if (IsProjectLockedByOtherTab?.Invoke(folder) == true)
        {
            Messages.Add(new MessageViewModel(MessageRole.System,
                $"Project \"{Path.GetFileName(folder)}\" is already open in another tab."));
            return;
        }

        UnlockCurrentProject?.Invoke();

        // Stop all chat sessions' CLI processes and close extra tabs
        StopAllChatSessions();
        _cliService.WorkingDirectory = folder;
        _settings.WorkingDirectory = folder;

        // Update recent folders
        RecentFolders.Remove(folder);
        RecentFolders.Insert(0, folder);
        _settings.RecentFolders.Remove(folder);
        _settings.RecentFolders.Insert(0, folder);

        while (RecentFolders.Count > 10)
            RecentFolders.RemoveAt(RecentFolders.Count - 1);
        while (_settings.RecentFolders.Count > 10)
            _settings.RecentFolders.RemoveAt(_settings.RecentFolders.Count - 1);

        _settingsService.Save(_settings);
        ProjectPath = folder;
        _registeredProjectRoots.TryAdd(Path.GetFullPath(folder), 0);

        LockProject?.Invoke(folder);
        RefreshGitStatus();
        StartGitRefreshTimer();
        UpdateExplorerRoot();

        _ = Task.Run(() =>
        {
            _projectRegistry.RegisterProject(folder, _gitService);
        }).ContinueWith(_ =>
            _onboardingService?.TryStartOnboarding(folder),
            TaskScheduler.FromCurrentSynchronizationContext());

        _ = Task.Run(() => RefreshAutocompleteIndex());

        if (_settings.ContextSnapshotEnabled)
        {
            _contextSnapshotService.InvalidateAll();
            _contextSnapshotService.StartGenerationInBackground(GetAllProjectPaths());
        }

        StartNewSession();

        if (showMessage)
        {
            var folderName = Path.GetFileName(folder) ?? folder;
            Messages.Add(new MessageViewModel(MessageRole.System,
                $"Project loaded: {folderName}\nType your message below to start working. Enter sends, Shift+Enter for newline."));
        }
    }

    private void StartNewSession()
    {
        // Delegate entirely to ActiveChatSession — it handles TechnicalWriter flush,
        // saved sessions cleanup, and context snapshot invalidation internally
        ActiveChatSession?.StartNewSession();
    }

    private void SwitchToOpus()
    {
        ActiveChatSession?.SwitchToOpus();
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
            catch { }
        };
        _gitRefreshTimer.Start();
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

        if (!string.IsNullOrEmpty(WorkingDirectory))
            _fileIndexService.BuildFileIndex(WorkingDirectory);
    }

    /// <summary>Save current chat history (delegate to ActiveChatSession).</summary>
    public void SaveChatHistory()
    {
        ActiveChatSession?.SaveChatHistory();
    }

    /// <summary>Load a chat from history into the active chat session.</summary>
    public void LoadChatFromHistory(ChatHistoryEntry entry)
    {
        // Switch project if different
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

        ActiveChatSession?.LoadChatFromHistory(entry);
    }

    public void StartGeneralChat()
    {
        StopAllChatSessions();
        _cliService.WorkingDirectory = null;
        _settings.WorkingDirectory = null;
        _settingsService.Save(_settings);

        _gitRefreshTimer?.Stop();
        _gitRefreshTimer = null;

        ProjectPath = "";
        StartNewSession();
    }

    /// <summary>
    /// Activates a multi-project workspace (or deactivates if null).
    /// Sets the primary project as CLI CWD and updates explorer to show all workspace roots.
    /// </summary>
    public void SetActiveWorkspace(Workspace? workspace)
    {
        ActiveWorkspace = workspace;

        if (workspace is null)
        {
            // Revert to single-project mode — keep current WorkingDirectory
            UpdateExplorerRoot();
            return;
        }

        // Note: callers (MainWindow) are responsible for calling WorkspaceService.TouchLastOpened()
        // before invoking this method — do not mutate workspace directly here.

        // Snapshot project paths to avoid race with concurrent workspace edits (e.g. WorkspaceWindow)
        var projectPaths = workspace.Projects.Select(p => p.Path).ToList();

        // Register all workspace projects
        foreach (var path in projectPaths)
            _registeredProjectRoots.TryAdd(Path.GetFullPath(path), 0);

        // Register all projects in the background
        _ = Task.Run(() =>
        {
            foreach (var path in projectPaths)
                _projectRegistry.RegisterProject(path, _gitService);
        });

        // Set primary project as CWD (no duplicate "Project loaded" message)
        if (!string.IsNullOrEmpty(workspace.PrimaryProjectPath))
            SetWorkingDirectoryCore(workspace.PrimaryProjectPath, showMessage: false);

        // Generate context snapshots for all workspace projects
        if (_settings.ContextSnapshotEnabled)
        {
            _contextSnapshotService.InvalidateAll();
            _contextSnapshotService.StartGenerationInBackground(GetAllProjectPaths());
        }

        var projectNames = string.Join(", ", projectPaths.Select(Path.GetFileName));
        Messages.Add(new MessageViewModel(MessageRole.System,
            $"Workspace \"{workspace.Name}\" loaded ({workspace.Projects.Count} projects: {projectNames})"));
    }

    /// <summary>
    /// Switches the primary (CLI CWD) project within the active workspace.
    /// </summary>
    public void SwitchPrimaryProject(string newPrimaryPath)
    {
        if (_activeWorkspace is null) return;

        var normalized = Path.GetFullPath(newPrimaryPath);
        if (!_activeWorkspace.Projects.Any(p =>
            string.Equals(Path.GetFullPath(p.Path), normalized, StringComparison.OrdinalIgnoreCase)))
            return;

        if (string.Equals(WorkingDirectory, normalized, StringComparison.OrdinalIgnoreCase))
            return; // Already primary

        StopAllChatSessions();

        _cliService.WorkingDirectory = normalized;
        _settings.WorkingDirectory = normalized;

        // Persist workspace primary change through WorkspaceService (thread-safe + saves)
        if (PersistWorkspacePrimary != null)
            PersistWorkspacePrimary(_activeWorkspace.Id, normalized);
        else
            _activeWorkspace.PrimaryProjectPath = normalized;

        _settingsService.Save(_settings);

        ProjectPath = normalized;
        RefreshGitStatus();
        UpdateExplorerRoot();

        _plannerService?.Configure(_cliService.ClaudeExePath, normalized);
        _planReviewerService?.Configure(_cliService.ClaudeExePath, normalized);
        _orchestratorService?.Configure(_cliService.ClaudeExePath, normalized);

        StartNewSession();

        var folderName = Path.GetFileName(normalized);
        Messages.Add(new MessageViewModel(MessageRole.System,
            $"Primary project switched to: {folderName}"));
    }

    /// <summary>
    /// Stops all chat sessions' CLI processes and closes extra chat tabs,
    /// leaving only the first chat session active.
    /// </summary>
    private void StopAllChatSessions()
    {
        // Flush TechnicalWriter buffers before switching projects
        _technicalWriterService?.Flush();

        // Stop all CLI processes
        foreach (var session in ChatSessions)
            session.CliService.StopSession();

        // Close extra chat tabs (keep only the first)
        var extraTabs = SubTabs.Where(t => t.Type == SubTabType.Chat).Skip(1).ToList();
        foreach (var tab in extraTabs)
        {
            if (tab.LinkedChatSession is { } session)
            {
                session.SaveChatHistory();
                ChatSessions.Remove(session);
                session.Dispose();
            }
            SubTabs.Remove(tab);
        }

        // Reset counter and rename remaining tab
        _chatTabCounter = 1;
        var chatTab = SubTabs.FirstOrDefault(t => t.Type == SubTabType.Chat);
        if (chatTab != null)
        {
            chatTab.Title = "Chat";
            if (ActiveSubTab?.Type == SubTabType.Chat)
                ActiveSubTab = chatTab;
        }
    }
}
