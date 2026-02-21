using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using ClaudeCodeWin.ContextSnapshot;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private const string SystemInstruction =
        """
        <system-instruction>
        ## Environment
        You are running inside **ClaudeCodeWin** — a WPF desktop GUI for Claude Code CLI on Windows.
        The user interacts with you through a chat interface, not a terminal. Keep this in mind when formatting output.

        ## GUI capabilities the user has access to
        - **Tasks menu**: user-configurable shell commands (deploy scripts, git commands, build, test, etc.) defined in `tasks.json` at `%APPDATA%\ClaudeCodeWin\tasks.json`. Each task has a name, command, optional project (for grouping into submenus), optional hotkey, and optional confirmation prompt. When the user asks to "add to tasks" or "add a task for deployment/publishing", they mean adding an entry to this tasks.json file so it appears in the Tasks menu and can be run with one click. Tasks with a `project` field are grouped into submenus by project name (e.g. Tasks > MyProject > Deploy). When creating tasks, always set the `project` field to the relevant project name so the menu stays organized.
        - **Scripts menu**: predefined prompts with variable substitution ({clipboard}, {git-status}, {git-diff}, {snapshot}, {file:path}) defined in `scripts.json` at `%APPDATA%\ClaudeCodeWin\scripts.json`. Scripts auto-send a prompt to you when clicked.
        - **File attachments**: the user can drag-and-drop files or paste screenshots (Ctrl+V) into the chat.
        - **Session persistence**: sessions are saved per project folder and restored on next launch (within 24h).
        - **Message queue**: messages sent while you are processing get queued and auto-sent sequentially.
        - **AskUserQuestion support**: When you use the AskUserQuestion tool, the user sees interactive buttons and can select an option. The selected answer is sent back to you as the next user message.

        ## Project registry
        - A `<project-registry>` section is injected at the start of each session with a list of all known local projects (path, git remote, tech stack, last opened date).
        - Use this to find projects on the local machine instead of searching external repositories.
        - The registry at `%APPDATA%\ClaudeCodeWin\project-registry.json` is auto-updated every time a project folder is opened.

        ## SSH access
        - A `<ssh-access>` section may be injected with Claude's SSH private key path and known servers.
        - When connecting via SSH or deploying, always use the configured SSH key with `-i` flag.
        - Refer to the known servers list for host/port/user details instead of asking the user.

        ## Important rules
        - When editing tasks.json or scripts.json, the format is a JSON array with camelCase keys. After editing, remind the user to click "Reload Tasks" or "Reload Scripts" in the menu.
        - When you finish a task, always write a clear completion marker in the user's communication language (e.g. "Готово", "Done", "Terminé") as a separate final line. This helps the app detect task completion and suggest relevant follow-up actions.
        - If the first fix attempt does not resolve a problem, stop guessing and start investigating: read logs, add diagnostics, trace the actual execution flow — determine the root cause before making the next fix. This rule does not apply to trivial failures like typos preventing a build/test from running.
        </system-instruction>
        """;

    private readonly ClaudeCliService _cliService;
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
    private TaskRunnerService? _taskRunnerService;
    private Window? _ownerWindow;
    private VersionInfo? _pendingUpdate;
    private string? _downloadedUpdatePath;

    private string _inputText = string.Empty;
    private bool _isProcessing;
    private string _statusText = "Ready";
    private string _modelName = "";
    private MessageViewModel? _currentAssistantMessage;
    private bool _showWelcome;
    private bool _isFirstDelta;
    private bool _hadToolsSinceLastText;
    private string _projectPath = "";
    private string _gitStatusText = "";
    private string _usageText = "";
    private string _contextUsageText = "";
    private string? _currentChatId;
    private string _ctaText = "";
    private CtaState _ctaState = CtaState.Welcome;
    private bool _isUpdating;
    private bool _showUpdateOverlay;
    private string _updateTitle = "";
    private string _updateStatusText = "";
    private int _updateProgressPercent;
    private bool _updateFailed;
    private bool _updateDownloading;
    private string _updateReleaseNotes = "";
    private bool _showDependencyOverlay;
    private string _dependencyTitle = "";
    private string _dependencySubtitle = "";
    private string _dependencyStep = "";
    private string _dependencyStatus = "Preparing...";
    private string _dependencyLog = "";
    private bool _dependencyFailed;
    private int _contextWindowSize;
    private bool _contextWarningShown;
    private int _previousInputTokens;
    private int _previousCtxPercent;
    private string _todoProgressText = "";
    private bool _showRateLimitBanner;
    private string _rateLimitCountdown = "";
    private bool _showProjectPicker;
    private bool _showTaskSuggestion;
    private System.Windows.Threading.DispatcherTimer? _taskSuggestionTimer;

    // Track project roots already registered this session (avoid re-registering)
    private readonly HashSet<string> _registeredProjectRoots =
        new(StringComparer.OrdinalIgnoreCase);

    // ExitPlanMode auto-confirm state
    private int _exitPlanModeAutoCount;

    // Control request protocol state
    private int _pendingQuestionCount;
    private string? _pendingControlRequestId;
    private string? _pendingControlToolUseId;
    private JsonElement? _pendingQuestionInput;
    private readonly List<(string question, string answer)> _pendingQuestionAnswers = [];

    public ObservableCollection<MessageViewModel> Messages { get; } = [];
    public ObservableCollection<FileAttachment> Attachments { get; } = [];
    public ObservableCollection<string> RecentFolders { get; } = [];
    public ObservableCollection<QueuedMessage> MessageQueue { get; } = [];
    public ObservableCollection<string> ChangedFiles { get; } = [];

    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    public bool IsUpdating
    {
        get => _isUpdating;
        set => SetProperty(ref _isUpdating, value);
    }

    public bool ShowUpdateOverlay
    {
        get => _showUpdateOverlay;
        set => SetProperty(ref _showUpdateOverlay, value);
    }

    public string UpdateTitle
    {
        get => _updateTitle;
        set => SetProperty(ref _updateTitle, value);
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        set => SetProperty(ref _updateStatusText, value);
    }

    public int UpdateProgressPercent
    {
        get => _updateProgressPercent;
        set => SetProperty(ref _updateProgressPercent, value);
    }

    public bool UpdateFailed
    {
        get => _updateFailed;
        set => SetProperty(ref _updateFailed, value);
    }

    public bool UpdateDownloading
    {
        get => _updateDownloading;
        set => SetProperty(ref _updateDownloading, value);
    }

    public string UpdateReleaseNotes
    {
        get => _updateReleaseNotes;
        set => SetProperty(ref _updateReleaseNotes, value);
    }

    public bool ShowDependencyOverlay
    {
        get => _showDependencyOverlay;
        set => SetProperty(ref _showDependencyOverlay, value);
    }

    public string DependencyTitle
    {
        get => _dependencyTitle;
        set => SetProperty(ref _dependencyTitle, value);
    }

    public string DependencySubtitle
    {
        get => _dependencySubtitle;
        set => SetProperty(ref _dependencySubtitle, value);
    }

    public string DependencyStep
    {
        get => _dependencyStep;
        set => SetProperty(ref _dependencyStep, value);
    }

    public string DependencyStatus
    {
        get => _dependencyStatus;
        set => SetProperty(ref _dependencyStatus, value);
    }

    public string DependencyLog
    {
        get => _dependencyLog;
        set => SetProperty(ref _dependencyLog, value);
    }

    public bool DependencyFailed
    {
        get => _dependencyFailed;
        set => SetProperty(ref _dependencyFailed, value);
    }

    public bool HasAttachments => Attachments.Count > 0;
    public bool HasQueuedMessages => MessageQueue.Count > 0;
    public bool HasChangedFiles => ChangedFiles.Count > 0;
    public string ChangedFilesText => $"{ChangedFiles.Count} file(s) changed";

    public bool ShowWelcome
    {
        get => _showWelcome;
        set => SetProperty(ref _showWelcome, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ModelName
    {
        get => _modelName;
        set
        {
            if (SetProperty(ref _modelName, value))
                OnPropertyChanged(nameof(CanSwitchToOpus));
        }
    }

    public bool CanSwitchToOpus =>
        !string.IsNullOrEmpty(_modelName)
        && !_modelName.Contains("opus", StringComparison.OrdinalIgnoreCase);

    public string ProjectPath
    {
        get => _projectPath;
        set => SetProperty(ref _projectPath, value);
    }

    public string GitStatusText
    {
        get => _gitStatusText;
        set => SetProperty(ref _gitStatusText, value);
    }

    public string UsageText
    {
        get => _usageText;
        set => SetProperty(ref _usageText, value);
    }

    public string ContextUsageText
    {
        get => _contextUsageText;
        set => SetProperty(ref _contextUsageText, value);
    }

    public string TodoProgressText
    {
        get => _todoProgressText;
        set => SetProperty(ref _todoProgressText, value);
    }

    public string CtaText
    {
        get => _ctaText;
        set => SetProperty(ref _ctaText, value);
    }

    public bool HasCta => !string.IsNullOrEmpty(_ctaText);

    public bool AutoConfirmEnabled
    {
        get => _settings.AutoConfirmPlanMode;
        set
        {
            _settings.AutoConfirmPlanMode = value;
            _settingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    public bool ShowRateLimitBanner
    {
        get => _showRateLimitBanner;
        set => SetProperty(ref _showRateLimitBanner, value);
    }

    public string RateLimitCountdown
    {
        get => _rateLimitCountdown;
        set => SetProperty(ref _rateLimitCountdown, value);
    }

    public bool ShowProjectPicker
    {
        get => _showProjectPicker;
        set => SetProperty(ref _showProjectPicker, value);
    }

    public bool ShowTaskSuggestion
    {
        get => _showTaskSuggestion;
        set => SetProperty(ref _showTaskSuggestion, value);
    }

    public ObservableCollection<TaskSuggestionItem> SuggestedTasks { get; } = [];

    public ObservableCollection<ProjectInfo> PickerProjects { get; } = [];

    public bool HasDialogHistory => Messages.Any(m => m.Role == MessageRole.Assistant);

    public string? WorkingDirectory => _cliService.WorkingDirectory;

    public RelayCommand SendCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand NewSessionCommand { get; }
    public RelayCommand RemoveAttachmentCommand { get; }
    public RelayCommand SelectFolderCommand { get; }
    public RelayCommand OpenRecentFolderCommand { get; }
    public RelayCommand RemoveRecentFolderCommand { get; }
    public RelayCommand PreviewAttachmentCommand { get; }
    public RelayCommand RemoveQueuedMessageCommand { get; }
    public RelayCommand SendQueuedNowCommand { get; }
    public RelayCommand ReturnQueuedToInputCommand { get; }
    public RelayCommand ViewChangedFileCommand { get; }
    public RelayCommand AnswerQuestionCommand { get; }
    public RelayCommand QuickPromptCommand { get; }
    public RelayCommand SwitchToOpusCommand { get; }
    public RelayCommand ExpandContextCommand { get; }
    public RelayCommand DismissRateLimitCommand { get; }
    public RelayCommand UpgradeAccountCommand { get; }
    public RelayCommand SelectProjectCommand { get; }
    public RelayCommand ContinueWithCurrentProjectCommand { get; }
    public RelayCommand RunSuggestedTaskCommand { get; }
    public RelayCommand DismissTaskSuggestionCommand { get; }
    public RelayCommand DismissTaskSuggestionForProjectCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public void SetUpdateChannel(string channel)
    {
        _updateService.UpdateChannel = channel;
    }

    public void StartUpdate()
    {
        if (_pendingUpdate is null) return;
        IsUpdating = true;
        UpdateDownloading = true;
        UpdateStatusText = "Starting download...";
        _ = _updateService.DownloadAndApplyAsync(_pendingUpdate);
    }

    public void DismissUpdate()
    {
        ShowUpdateOverlay = false;
        UpdateFailed = false;
        UpdateDownloading = false;
        IsUpdating = false;
    }

    public void SetTaskRunner(TaskRunnerService taskRunnerService, Window ownerWindow)
    {
        _taskRunnerService = taskRunnerService;
        _ownerWindow = ownerWindow;
    }

    private void StopTaskSuggestionTimer()
    {
        _taskSuggestionTimer?.Stop();
        _taskSuggestionTimer = null;
    }

    public MainViewModel(ClaudeCliService cliService, NotificationService notificationService,
        SettingsService settingsService, AppSettings settings, GitService gitService,
        UpdateService updateService, FileIndexService fileIndexService,
        ChatHistoryService chatHistoryService, ProjectRegistryService projectRegistry,
        ContextSnapshotService contextSnapshotService, UsageService usageService)
    {
        _cliService = cliService;
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

        SendCommand = new RelayCommand(() => _ = SendMessageAsync());
        CancelCommand = new RelayCommand(CancelProcessing, () => IsProcessing);
        NewSessionCommand = new RelayCommand(StartNewSession);
        RemoveAttachmentCommand = new RelayCommand(p =>
        {
            if (p is FileAttachment att)
                Attachments.Remove(att);
        });
        PreviewAttachmentCommand = new RelayCommand(p =>
        {
            if (p is FileAttachment att && att.IsImage && File.Exists(att.FilePath))
                ShowImagePreview(att);
        });

        SelectFolderCommand = new RelayCommand(SelectFolder);
        OpenRecentFolderCommand = new RelayCommand(p =>
        {
            if (p is string folder)
                SetWorkingDirectory(folder);
        });
        RemoveRecentFolderCommand = new RelayCommand(p =>
        {
            if (p is string folder)
            {
                RecentFolders.Remove(folder);
                _settings.RecentFolders.Remove(folder);
                _settingsService.Save(_settings);
            }
        });
        CheckForUpdatesCommand = new AsyncRelayCommand(async () =>
        {
            StatusText = "Checking for updates...";
            var update = await _updateService.CheckForUpdateAsync();
            if (update is null)
            {
                StatusText = "Ready";
                MessageBox.Show($"You are on the latest version ({_updateService.CurrentVersion}).",
                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            // If update found, OnUpdateAvailable handler will show overlay
        });

        RemoveQueuedMessageCommand = new RelayCommand(p =>
        {
            if (p is QueuedMessage qm)
                MessageQueue.Remove(qm);
        });
        SendQueuedNowCommand = new RelayCommand(p =>
        {
            if (p is QueuedMessage qm)
            {
                MessageQueue.Remove(qm);
                CancelProcessing();
                _ = SendDirectAsync(qm.Text, qm.Attachments);
            }
        });
        ReturnQueuedToInputCommand = new RelayCommand(p =>
        {
            if (p is QueuedMessage qm)
            {
                MessageQueue.Remove(qm);
                InputText = qm.Text;
                // Restore attachments back to the attachment bar
                if (qm.Attachments is not null)
                {
                    foreach (var att in qm.Attachments)
                        AddAttachment(att);
                }
            }
        });
        ViewChangedFileCommand = new RelayCommand(p =>
        {
            if (p is string filePath)
                ShowFileDiff(filePath);
        });
        AnswerQuestionCommand = new RelayCommand(p =>
        {
            if (p is not string answer) return;

            if (_pendingControlRequestId is not null)
                HandleControlAnswer(answer);
        });
        QuickPromptCommand = new RelayCommand(p =>
        {
            if (p is string prompt)
                _ = SendDirectAsync(prompt, null);
        });
        SwitchToOpusCommand = new RelayCommand(SwitchToOpus);
        ExpandContextCommand = new RelayCommand(ExpandContext);
        DismissRateLimitCommand = new RelayCommand(() => ShowRateLimitBanner = false);
        UpgradeAccountCommand = new RelayCommand(() =>
        {
            try { Process.Start(new ProcessStartInfo("https://console.anthropic.com/settings/billing") { UseShellExecute = true }); }
            catch { }
        });
        SelectProjectCommand = new RelayCommand(p =>
        {
            if (p is string path)
            {
                ShowProjectPicker = false;
                SetWorkingDirectory(path);
            }
        });
        ContinueWithCurrentProjectCommand = new RelayCommand(() => ShowProjectPicker = false);
        RunSuggestedTaskCommand = new RelayCommand(p =>
        {
            if (p is TaskSuggestionItem item)
            {
                ShowTaskSuggestion = false;
                StopTaskSuggestionTimer();
                if (item.IsCommit)
                    _ = SendDirectAsync("/commit", null);
                else if (item.Task is not null && _ownerWindow is not null)
                    TaskRunnerService.RunTaskPublic(item.Task, this, _ownerWindow);
            }
        });
        DismissTaskSuggestionCommand = new RelayCommand(() =>
        {
            ShowTaskSuggestion = false;
            StopTaskSuggestionTimer();
        });
        DismissTaskSuggestionForProjectCommand = new RelayCommand(() =>
        {
            ShowTaskSuggestion = false;
            StopTaskSuggestionTimer();
            if (!string.IsNullOrEmpty(WorkingDirectory))
            {
                var normalized = WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!_settings.TaskSuggestionDismissedProjects.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.TaskSuggestionDismissedProjects.Add(normalized);
                    _settingsService.Save(_settings);
                }
            }
        });

        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDialogHistory));
        MessageQueue.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQueuedMessages));
        ChangedFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasChangedFiles));
            OnPropertyChanged(nameof(ChangedFilesText));
        };

        // Subscribe to update events
        _updateService.OnUpdateAvailable += info =>
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _pendingUpdate = info;
                UpdateTitle = $"v{_updateService.CurrentVersion}  →  v{info.Version}";
                UpdateReleaseNotes = info.ReleaseNotes ?? "";
                UpdateStatusText = "A new version is available.";
                UpdateProgressPercent = 0;
                UpdateFailed = false;
                UpdateDownloading = false;
                ShowUpdateOverlay = true;
            });
        };

        _updateService.OnDownloadProgress += percent =>
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpdateProgressPercent = percent;
                UpdateStatusText = $"Downloading update... {percent}%";
            });
        };

        _updateService.OnUpdateReady += path =>
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _downloadedUpdatePath = path;
                UpdateStatusText = "Update ready — restarting...";
                UpdateProgressPercent = 100;
                UpdateService.ApplyUpdate(path);
            });
        };

        _updateService.OnError += error =>
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsUpdating = false;
                UpdateFailed = true;
                UpdateDownloading = false;
                UpdateStatusText = error;
            });
        };

        // Start periodic update checks
        _updateService.UpdateChannel = settings.UpdateChannel ?? "stable";
        _updateService.StartPeriodicCheck();

        _cliService.OnTextBlockStart += HandleTextBlockStart;
        _cliService.OnTextDelta += HandleTextDelta;
        _cliService.OnToolUseStarted += HandleToolUseStarted;
        _cliService.OnToolResult += HandleToolResult;
        _cliService.OnCompleted += HandleCompleted;
        _cliService.OnError += HandleError;
        _cliService.OnControlRequest += HandleControlRequest;
        _cliService.OnFileChanged += HandleFileChanged;
        _cliService.OnRateLimitDetected += () =>
            Application.Current.Dispatcher.InvokeAsync(() => _usageService.SetRateLimitedExternally());

        // Subscribe to rate limit changes from UsageService
        _usageService.OnRateLimitChanged += isLimited =>
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (isLimited)
                {
                    ShowRateLimitBanner = true;
                    RateLimitCountdown = _usageService.GetSessionCountdown();
                    Messages.Add(new MessageViewModel(MessageRole.System,
                        $"Rate limit reached. Resets in {RateLimitCountdown}."));
                }
                else
                {
                    ShowRateLimitBanner = false;
                    RateLimitCountdown = "";
                    Messages.Add(new MessageViewModel(MessageRole.System,
                        "Rate limit cleared. You can continue working."));
                }
            });
        };

        // Update rate limit countdown text every second (piggybacking on UsageService OnUsageUpdated)
        _usageService.OnUsageUpdated += () =>
        {
            if (_showRateLimitBanner)
                RateLimitCountdown = _usageService.GetSessionCountdown();
        };

        _cliService.OnCompactionDetected += msg =>
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var ctx = ContextUsageText;
                Messages.Add(new MessageViewModel(MessageRole.System,
                    $"Context auto-compacted. {msg} [{ctx}]"));
                DiagnosticLogger.Log("COMPACTION", $"{msg} ctx={ctx}");
                _contextWarningShown = false; // reset so warning fires again if context fills up
            });
        };

        _cliService.OnSystemNotification += msg =>
        {
            DiagnosticLogger.Log("SYSTEM_NOTIFICATION", msg);
        };

        // Enable diagnostic logging from settings
        DiagnosticLogger.Enabled = settings.DiagnosticLoggingEnabled;

        // Initialize recent folders from settings
        foreach (var folder in settings.RecentFolders)
            RecentFolders.Add(folder);

        ShowWelcome = string.IsNullOrEmpty(settings.WorkingDirectory);
        ProjectPath = settings.WorkingDirectory ?? "";
        UpdateCta(ShowWelcome ? CtaState.Welcome : CtaState.Ready);

        // Restore session and git status if project was already set
        if (!string.IsNullOrEmpty(settings.WorkingDirectory))
        {
            _registeredProjectRoots.Add(Path.GetFullPath(settings.WorkingDirectory));
            RefreshGitStatus();
            _ = Task.Run(() => RefreshAutocompleteIndex());
            _ = Task.Run(() => _projectRegistry.RegisterProject(settings.WorkingDirectory, _gitService));

            // Generate context snapshots for recent projects from registry (not just current dir)
            if (_settings.ContextSnapshotEnabled)
            {
                var recentPaths = _projectRegistry.GetMostRecentProjects(5).Select(p => p.Path).ToList();
                _contextSnapshotService.StartGenerationInBackground(recentPaths);
            }

            if (settings.SavedSessions.TryGetValue(settings.WorkingDirectory, out var saved)
                && DateTime.Now - saved.CreatedAt < TimeSpan.FromHours(24))
            {
                _cliService.RestoreSession(saved.SessionId);
                var resumeTime = saved.CreatedAt.ToString("HH:mm");
                Messages.Add(new MessageViewModel(MessageRole.System,
                    $"Resumed session from {resumeTime}. Type your message to continue."));
            }
        }
    }
}

internal enum CtaState
{
    Welcome,
    Ready,
    Processing,
    WaitingForUser,
    AnswerQuestion,
    ConfirmOperation
}
