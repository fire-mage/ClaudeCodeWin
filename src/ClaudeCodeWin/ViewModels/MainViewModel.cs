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
        - **AskUserQuestion support**: When you use the AskUserQuestion tool, the user sees interactive buttons and can select options or provide custom text input.

        ## Project registry
        - A `<project-registry>` section is injected at the start of each session with a list of all known local projects (path, git remote, tech stack, last opened date).
        - Use this to find projects on the local machine instead of searching external repositories.
        - The registry at `%APPDATA%\ClaudeCodeWin\project-registry.json` is auto-updated every time a project folder is opened.

        ## SSH access
        - A `<ssh-access>` section may be injected with Claude's SSH private key path and known servers.
        - When connecting via SSH or deploying, always use the configured SSH key with `-i` flag.
        - Refer to the known servers list for host/port/user details instead of asking the user.

        ## Windows Shell Safety
        **NEVER** use `/dev/null` in Bash commands (e.g. `2>/dev/null`, `> /dev/null`). On Windows, this creates a literal file named `nul` which can break cloud sync (OneDrive, Dropbox, etc.). Use `2>&1` to merge streams, or `|| true` to suppress errors.

        ## Important rules
        - When editing tasks.json or scripts.json, the format is a JSON array with camelCase keys. After editing, remind the user to click "Reload Tasks" or "Reload Scripts" in the menu.
        - When you finish a task, write a brief summary of what was done and end with a completion word (e.g. "Done", "Готово") on a separate final line. Separate the summary from the working process with a horizontal rule (---). The app renders this section as a styled summary panel.
        - If the first fix attempt does not resolve a problem, stop guessing and start investigating: read logs, add diagnostics, trace the actual execution flow — determine the root cause before making the next fix. This rule does not apply to trivial failures like typos preventing a build/test from running.
        - Minimize external dependencies. Any significant new dependency should be confirmed with the user before adding.
        - When implementing a new feature or starting a new project, suggest writing tests first — they serve as a contract between you and the user, making it clear how to verify the work is complete.
        - After completing significant development or refactoring work, suggest running a code quality review and a security vulnerability analysis.
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
    private string _statusText = "";
    private string _modelName = "";
    private MessageViewModel? _currentAssistantMessage;
    private bool _showWelcome;
    private bool _isFirstDelta;
    private bool _hadToolsSinceLastText;
    private bool _hasResponseStarted;
    private string? _lastSentText;
    private List<FileAttachment>? _lastSentAttachments;
    private string _projectPath = "";
    private string _gitStatusText = "";
    private string _gitDirtyText = "";
    private bool _hasGitRepo;
    private string _usageText = "";
    private string _sessionPctText = "";
    private string _sessionExtraText = "";
    private string _weekPctText = "";
    private string _contextUsageText = "";
    private string _contextPctText = "";
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
    private bool _showTaskSuggestion;
    private bool _showFinalizeActionsLabel;
    private bool _hasCompletedTask;
    private bool _finalizeLabelBlinking;
    private int _finalizeCountdown;
    private System.Windows.Threading.DispatcherTimer? _taskSuggestionTimer;
    private System.Windows.Threading.DispatcherTimer? _blinkTimer;

    /// <summary>Set by View to animate popup collapse instead of instant hide.</summary>
    public Action? OnFinalizeCollapse { get; set; }

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

    public bool IsContextExpanded =>
        _cliService.ModelOverride?.Contains("[1m]") == true;

    public string ExpandContextMenuHeader =>
        IsContextExpanded ? "Reduce Context (1M -> 200k Tokens)" : "Expand Context (1M Tokens)";

    public string ProjectPath
    {
        get => _projectPath;
        set
        {
            if (SetProperty(ref _projectPath, value))
            {
                OnPropertyChanged(nameof(ProjectParentPath));
                OnPropertyChanged(nameof(ProjectFolderName));
            }
        }
    }

    public string ProjectParentPath
    {
        get
        {
            if (string.IsNullOrEmpty(_projectPath)) return "";
            var trimmed = _projectPath.TrimEnd('\\', '/');
            var lastSep = trimmed.LastIndexOfAny(['\\', '/']);
            return lastSep >= 0 ? trimmed[..(lastSep + 1)] : "";
        }
    }

    public string ProjectFolderName
    {
        get
        {
            if (string.IsNullOrEmpty(_projectPath)) return "";
            var trimmed = _projectPath.TrimEnd('\\', '/');
            var lastSep = trimmed.LastIndexOfAny(['\\', '/']);
            return lastSep >= 0 ? trimmed[(lastSep + 1)..] : trimmed;
        }
    }

    public string GitStatusText
    {
        get => _gitStatusText;
        set => SetProperty(ref _gitStatusText, value);
    }

    public string GitDirtyText
    {
        get => _gitDirtyText;
        set => SetProperty(ref _gitDirtyText, value);
    }

    public bool HasGitRepo
    {
        get => _hasGitRepo;
        set => SetProperty(ref _hasGitRepo, value);
    }

    public string UsageText
    {
        get => _usageText;
        set => SetProperty(ref _usageText, value);
    }

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

    public string ContextUsageText
    {
        get => _contextUsageText;
        set => SetProperty(ref _contextUsageText, value);
    }

    public string ContextPctText
    {
        get => _contextPctText;
        set => SetProperty(ref _contextPctText, value);
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

    public bool ShowTaskSuggestion
    {
        get => _showTaskSuggestion;
        set => SetProperty(ref _showTaskSuggestion, value);
    }

    public bool ShowFinalizeActionsLabel
    {
        get => _showFinalizeActionsLabel;
        set => SetProperty(ref _showFinalizeActionsLabel, value);
    }

    public bool HasCompletedTask
    {
        get => _hasCompletedTask;
        set => SetProperty(ref _hasCompletedTask, value);
    }

    public int FinalizeCountdown
    {
        get => _finalizeCountdown;
        set => SetProperty(ref _finalizeCountdown, value);
    }

    public bool FinalizeLabelBlinking
    {
        get => _finalizeLabelBlinking;
        set => SetProperty(ref _finalizeLabelBlinking, value);
    }

    public ObservableCollection<TaskSuggestionItem> SuggestedTasks { get; } = [];

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
    public RelayCommand SwitchToOpusCommand { get; }
    public RelayCommand ExpandContextCommand { get; }
    public RelayCommand ReduceContextCommand { get; }
    public RelayCommand DismissRateLimitCommand { get; }
    public RelayCommand UpgradeAccountCommand { get; }
    public RelayCommand RunSuggestedTaskCommand { get; }
    public RelayCommand CloseFinalizePopupCommand { get; }
    public RelayCommand OpenFinalizeActionsCommand { get; }
    public RelayCommand DontSuggestForProjectCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    /// <summary>
    /// Returns the built-in CCW system instruction text for display in the Instructions editor.
    /// </summary>
    public static string GetSystemInstructionText() => SystemInstruction;

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

    private void StopBlinkTimer()
    {
        _blinkTimer?.Stop();
        _blinkTimer = null;
        FinalizeLabelBlinking = false;
    }

    private void StartAutoCollapseTimer()
    {
        StopTaskSuggestionTimer();
        FinalizeCountdown = 60;
        _taskSuggestionTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _taskSuggestionTimer.Tick += (_, _) =>
        {
            FinalizeCountdown--;
            if (FinalizeCountdown <= 0)
                CollapseToFinalizeLabel();
        };
        _taskSuggestionTimer.Start();
    }

    private void CollapseToFinalizeLabel()
    {
        StopTaskSuggestionTimer();
        FinalizeCountdown = 0;
        var showLabel = SuggestedTasks.Count > 0;

        if (ShowTaskSuggestion && OnFinalizeCollapse is not null)
        {
            // Animate, then show label
            ShowFinalizeActionsLabel = showLabel;
            if (showLabel) StartBlinkTimer();
            OnFinalizeCollapse();
        }
        else
        {
            ShowTaskSuggestion = false;
            if (showLabel)
            {
                ShowFinalizeActionsLabel = true;
                StartBlinkTimer();
            }
        }
    }

    private void StartBlinkTimer()
    {
        StopBlinkTimer();
        FinalizeLabelBlinking = true;
        var elapsed = 0;
        _blinkTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _blinkTimer.Tick += (_, _) =>
        {
            elapsed++;
            if (elapsed >= 10) // 5 seconds (10 x 500ms)
                StopBlinkTimer();
        };
        _blinkTimer.Start();
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
                StatusText = "";
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
        SwitchToOpusCommand = new RelayCommand(SwitchToOpus);
        ExpandContextCommand = new RelayCommand(ExpandContext);
        ReduceContextCommand = new RelayCommand(ReduceContext);
        DismissRateLimitCommand = new RelayCommand(() => ShowRateLimitBanner = false);
        UpgradeAccountCommand = new RelayCommand(() =>
        {
            try { Process.Start(new ProcessStartInfo("https://console.anthropic.com/settings/billing") { UseShellExecute = true }); }
            catch { }
        });
        RunSuggestedTaskCommand = new RelayCommand(p =>
        {
            if (p is TaskSuggestionItem item && !item.IsCompleted)
            {
                // Mark as completed immediately (optimistic)
                item.IsCompleted = true;
                item.CompletedStatusText = item.IsCommit ? "Committed" : $"Ran {item.Label}";

                // Collapse popup to label
                CollapseToFinalizeLabel();

                if (item.IsCommit)
                    _ = SendDirectAsync("Review the current git changes (staged and unstaged), create a commit with an appropriate message, and push to the remote repository.", null);
                else if (item.Task is not null && _ownerWindow is not null)
                    TaskRunnerService.RunTaskPublic(item.Task, this, _ownerWindow);
            }
        });
        CloseFinalizePopupCommand = new RelayCommand(CollapseToFinalizeLabel);
        OpenFinalizeActionsCommand = new RelayCommand(() =>
        {
            if (SuggestedTasks.Count > 0)
            {
                StopBlinkTimer();
                ShowTaskSuggestion = true;
                ShowFinalizeActionsLabel = false;
                StartAutoCollapseTimer();
            }
        });
        DontSuggestForProjectCommand = new RelayCommand(() =>
        {
            ShowTaskSuggestion = false;
            ShowFinalizeActionsLabel = false;
            HasCompletedTask = false;
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

        _cliService.OnMessageStarted += HandleMessageStarted;

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
