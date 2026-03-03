using System.Collections.ObjectModel;
using System.ComponentModel;
using ClaudeCodeWin.ContextSnapshot;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

/// <summary>
/// Manages multiple MainViewModel tabs for parallel project work.
/// Each tab runs an independent Claude CLI session.
/// </summary>
public class TabHostViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly GitService _gitService;
    private readonly UpdateService _updateService;
    private readonly FileIndexService _fileIndexService;
    private readonly ChatHistoryService _chatHistoryService;
    private readonly ProjectRegistryService _projectRegistry;
    private readonly ContextSnapshotService _contextSnapshotService;
    private readonly UsageService _usageService;
    private readonly BacklogService _backlogService;
    private readonly TeamNotesService _teamNotesService;
    private readonly DevKbService? _devKbService;

    // Project uniqueness: prevent the same project from being open in two tabs
    private readonly HashSet<string> _openProjects = new(StringComparer.OrdinalIgnoreCase);

    // Track Team PropertyChanged handlers for proper unsubscribe on tab close
    private readonly Dictionary<MainViewModel, System.ComponentModel.PropertyChangedEventHandler> _teamHandlers = new();

    private MainViewModel? _activeTab;
    private string _sessionPctText = "";
    private string _sessionExtraText = "";
    private string _weekPctText = "";
    private string _weekExtraText = "";
    private string _usageText = "";
    private bool _isTabPanelCompact;
    private bool _isTeamPanelVisible;

    // CLI executable path (shared across all tabs)
    public string ClaudeExePath { get; set; } = "claude";

    // Backlog service (shared across all tabs)
    public BacklogService Backlog => _backlogService;

    public ObservableCollection<MainViewModel> Tabs { get; } = [];

    public MainViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (_activeTab == value) return;

            // Unsubscribe from old tab and mark inactive
            if (_activeTab is not null)
            {
                _activeTab.PropertyChanged -= ActiveTab_PropertyChanged;
                _activeTab.IsActiveTab = false;
            }

            _activeTab = value;

            // Subscribe to new tab and mark active
            if (_activeTab is not null)
            {
                _activeTab.PropertyChanged += ActiveTab_PropertyChanged;
                _activeTab.IsActiveTab = true;
            }

            OnPropertyChanged();
            RaiseAllStatusBarProperties();
        }
    }

    public bool ShowTabStrip => Tabs.Count > 1;

    // Global usage properties (same for all tabs — API-level, not per-session)
    public string SessionPctText
    {
        get => _sessionPctText;
        set => SetProperty(ref _sessionPctText, value);
    }

    public string SessionExtraText
    {
        get => _sessionExtraText;
        set => SetProperty(ref _sessionExtraText, value);
    }

    public string WeekPctText
    {
        get => _weekPctText;
        set => SetProperty(ref _weekPctText, value);
    }

    public string WeekExtraText
    {
        get => _weekExtraText;
        set => SetProperty(ref _weekExtraText, value);
    }

    public string UsageText
    {
        get => _usageText;
        set => SetProperty(ref _usageText, value);
    }

    // UpdateViewModel is global (app updates apply to the whole app, not per-tab)
    public UpdateViewModel Update { get; }

    public bool IsTabPanelCompact
    {
        get => _isTabPanelCompact;
        set
        {
            if (SetProperty(ref _isTabPanelCompact, value))
                OnPropertyChanged(nameof(IsTabPanelFull));
        }
    }

    public bool IsTabPanelFull => !_isTabPanelCompact;

    public bool IsTeamPanelVisible
    {
        get => _isTeamPanelVisible;
        set => SetProperty(ref _isTeamPanelVisible, value);
    }

    public int TotalPendingCount => Tabs.Sum(t => t.Team?.PendingTaskCount ?? 0);

    public string TeamButtonText => TotalPendingCount > 0 ? $"Team ({TotalPendingCount})" : "Team";

    public RelayCommand ToggleTeamPanelCommand { get; }
    public RelayCommand CloseTabCommand { get; }
    public RelayCommand ToggleTabPanelCompactCommand { get; }

    /// <summary>
    /// Raised when the active tab changes, so MainWindow can re-subscribe to per-tab events.
    /// </summary>
    public event Action? OnActiveTabChanged;

    /// <summary>
    /// Raised before compact mode toggle, so MainWindow can save the current panel width.
    /// </summary>
    public event Action? OnBeforeCompactToggle;

    public TabHostViewModel(
        NotificationService notificationService,
        SettingsService settingsService,
        AppSettings settings,
        GitService gitService,
        UpdateService updateService,
        FileIndexService fileIndexService,
        ChatHistoryService chatHistoryService,
        ProjectRegistryService projectRegistry,
        ContextSnapshotService contextSnapshotService,
        UsageService usageService,
        BacklogService backlogService,
        TeamNotesService teamNotesService,
        DevKbService? devKbService = null)
    {
        _notificationService = notificationService;
        _settingsService = settingsService;
        _settings = settings;
        _gitService = gitService;
        _updateService = updateService;
        _fileIndexService = fileIndexService;
        _chatHistoryService = chatHistoryService;
        _projectRegistry = projectRegistry;
        _contextSnapshotService = contextSnapshotService;
        _usageService = usageService;
        _backlogService = backlogService;
        _teamNotesService = teamNotesService;
        _devKbService = devKbService;

        _isTabPanelCompact = settings.TabPanelCompact;

        Update = new UpdateViewModel(updateService, settings);
        Update.OnStatusTextChange += text =>
        {
            if (_activeTab is not null)
                _activeTab.StatusText = text;
        };

        ToggleTeamPanelCommand = new RelayCommand(() =>
        {
            IsTeamPanelVisible = !IsTeamPanelVisible;
            if (IsTeamPanelVisible)
            {
                foreach (var tab in Tabs)
                    tab.Team?.Refresh();
            }
        });

        CloseTabCommand = new RelayCommand(p =>
        {
            if (p is MainViewModel tab)
                CloseTab(tab);
            else if (ActiveTab is not null)
                CloseTab(ActiveTab);
        });
        ToggleTabPanelCompactCommand = new RelayCommand(() =>
        {
            OnBeforeCompactToggle?.Invoke();
            IsTabPanelCompact = !IsTabPanelCompact;
            _settings.TabPanelCompact = IsTabPanelCompact;
            _settingsService.Save(_settings);
        });

        Tabs.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(ShowTabStrip));
            RefreshTeamBadge();
        };
    }

    /// <summary>
    /// Subscribes to a tab's Team.PendingTaskCount changes to update the global badge.
    /// </summary>
    private void SubscribeToTeamChanges(MainViewModel tab)
    {
        // Team is initialized in MainViewModel constructor (InitializeSubTabs),
        // so it's always available by the time CreateTab returns.
        if (tab.Team is null)
        {
            DiagnosticLogger.Log("TEAM_SUBSCRIBE", "WARNING: Team is null, badge won't update for this tab");
            return;
        }

        System.ComponentModel.PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(TeamViewModel.PendingTaskCount))
                RefreshTeamBadge();
        };
        tab.Team.PropertyChanged += handler;
        _teamHandlers[tab] = handler;
    }

    private void UnsubscribeTeamChanges(MainViewModel tab)
    {
        if (_teamHandlers.Remove(tab, out var handler) && tab.Team is not null)
            tab.Team.PropertyChanged -= handler;
    }

    private void RefreshTeamBadge()
    {
        OnPropertyChanged(nameof(TotalPendingCount));
        OnPropertyChanged(nameof(TeamButtonText));
    }

    public MainViewModel CreateTab()
    {
        var cliService = new ClaudeCliService();
        cliService.ClaudeExePath = ClaudeExePath;

        var tab = new MainViewModel(
            cliService, _notificationService, _settingsService, _settings,
            _gitService, _fileIndexService, _chatHistoryService,
            _projectRegistry, _contextSnapshotService, _usageService,
            _backlogService, _teamNotesService, _devKbService);

        // Wire project locking callbacks
        tab.IsProjectLockedByOtherTab = path =>
        {
            var normalized = System.IO.Path.GetFullPath(path);
            return _openProjects.Contains(normalized) &&
                   !string.Equals(tab.WorkingDirectory, path, StringComparison.OrdinalIgnoreCase);
        };
        tab.LockProject = path =>
        {
            var normalized = System.IO.Path.GetFullPath(path);
            _openProjects.Add(normalized);
            // Raise TabTitle changed on this tab
            tab.RaiseTabTitleChanged();
        };
        tab.UnlockCurrentProject = () =>
        {
            if (!string.IsNullOrEmpty(tab.WorkingDirectory))
                _openProjects.Remove(System.IO.Path.GetFullPath(tab.WorkingDirectory));
        };

        Tabs.Add(tab);
        ActiveTab = tab;

        // Subscribe to Team pipeline changes for global badge
        SubscribeToTeamChanges(tab);

        return tab;
    }

    public void CloseTab(MainViewModel tab)
    {
        // Cancel processing if active
        if (tab.IsProcessing)
            tab.CancelCommand.Execute(null);

        // Unlock project
        if (!string.IsNullOrEmpty(tab.WorkingDirectory))
            _openProjects.Remove(System.IO.Path.GetFullPath(tab.WorkingDirectory));

        // Clean up resources
        UnsubscribeTeamChanges(tab);
        tab.Dispose();

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (Tabs.Count == 0)
        {
            // Cannot have zero tabs — create a fresh one
            var fresh = CreateTab();
            fresh.ShowWelcome = true;
        }
        else if (ActiveTab == tab || ActiveTab is null)
        {
            // Switch to adjacent tab
            ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    public void SwitchToNextTab()
    {
        if (Tabs.Count < 2 || ActiveTab is null) return;
        var index = Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(index + 1) % Tabs.Count];
    }

    public void SwitchToPreviousTab()
    {
        if (Tabs.Count < 2 || ActiveTab is null) return;
        var index = Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(index - 1 + Tabs.Count) % Tabs.Count];
    }

    public void SwitchToTab(int index)
    {
        if (index >= 0 && index < Tabs.Count)
            ActiveTab = Tabs[index];
    }

    /// <summary>
    /// Disposes all tabs (called when the application is closing).
    /// </summary>
    public void DisposeAll()
    {
        foreach (var tab in Tabs)
        {
            UnsubscribeTeamChanges(tab);
            tab.Dispose();
        }
    }

    /// <summary>
    /// Check if a project path is already open in any tab.
    /// </summary>
    public bool IsProjectOpen(string path)
    {
        var normalized = System.IO.Path.GetFullPath(path);
        return _openProjects.Contains(normalized);
    }

    private void ActiveTab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != _activeTab) return;

        // Forward status-bar-relevant property changes
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.StatusText):
            case nameof(MainViewModel.ModelName):
            case nameof(MainViewModel.ProjectPath):
            case nameof(MainViewModel.ProjectParentPath):
            case nameof(MainViewModel.ProjectFolderName):
            case nameof(MainViewModel.EffectiveProjectName):
            case nameof(MainViewModel.GitDirtyText):
            case nameof(MainViewModel.HasGitRepo):
            case nameof(MainViewModel.ContextPctText):
            case nameof(MainViewModel.ShowRateLimitBanner):
            case nameof(MainViewModel.RateLimitCountdown):
            case nameof(MainViewModel.TabTitle):
            case nameof(MainViewModel.HasNotification):
                // These are read via {Binding ActiveTab.PropertyName} in XAML
                // but we also fire our own change to update any direct bindings
                OnPropertyChanged($"ActiveTab.{e.PropertyName}");
                break;
        }
    }

    private void RaiseAllStatusBarProperties()
    {
        OnPropertyChanged(nameof(ActiveTab));
        OnActiveTabChanged?.Invoke();
    }
}
