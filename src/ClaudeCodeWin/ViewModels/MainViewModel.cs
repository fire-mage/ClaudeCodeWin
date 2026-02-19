using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeCodeWin.ContextSnapshot;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public class MainViewModel : ViewModelBase
{
    private const string SystemInstruction =
        """
        <system-instruction>
        ## Environment
        You are running inside **ClaudeCodeWin** — a WPF desktop GUI for Claude Code CLI on Windows.
        The user interacts with you through a chat interface, not a terminal. Keep this in mind when formatting output.

        ## GUI capabilities the user has access to
        - **Tasks menu**: user-configurable shell commands (deploy scripts, git commands, build, test, etc.) defined in `tasks.json` at `%APPDATA%\ClaudeCodeWin\tasks.json`. Each task has a name, command, optional hotkey, and optional confirmation prompt. When the user asks to "add to tasks" or "add a task for deployment/publishing", they mean adding an entry to this tasks.json file so it appears in the Tasks menu and can be run with one click.
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
    private bool _showDependencyOverlay;
    private string _dependencyTitle = "";
    private string _dependencySubtitle = "";
    private string _dependencyStep = "";
    private string _dependencyStatus = "Preparing...";
    private string _dependencyLog = "";
    private bool _dependencyFailed;
    private int _contextWindowSize;
    private bool _contextWarningShown;
    private string _todoProgressText = "";
    private bool _showRateLimitBanner;
    private string _rateLimitCountdown = "";
    private bool _showProjectPicker;

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

    public ObservableCollection<ProjectInfo> PickerProjects { get; } = [];

    public bool HasDialogHistory => Messages.Any(m => m.Role == MessageRole.Assistant);

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
    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public void SetUpdateChannel(string channel)
    {
        _updateService.UpdateChannel = channel;
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
                var notes = string.IsNullOrEmpty(info.ReleaseNotes) ? "" : $"\n\n{info.ReleaseNotes}";
                var result = MessageBox.Show(
                    $"Version {info.Version} is available (current: {_updateService.CurrentVersion}).{notes}\n\nDownload and install update?",
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    IsUpdating = true;
                    _ = _updateService.DownloadAndApplyAsync(info);
                }
            });
        };

        _updateService.OnDownloadProgress += percent =>
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
                StatusText = $"Downloading update... {percent}%");
        };

        _updateService.OnUpdateReady += path =>
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _downloadedUpdatePath = path;
                StatusText = "Update ready — restarting...";
                UpdateService.ApplyUpdate(path);
            });
        };

        _updateService.OnError += error =>
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsUpdating = false;
                StatusText = "Ready";
                MessageBox.Show(error, "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            StatusText = "Ready";

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

    private void HandleControlRequest(string requestId, string toolName, string toolUseId, JsonElement input)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (toolName == "ExitPlanMode")
            {
                HandleExitPlanModeControl(requestId, toolUseId);
            }
            else if (toolName == "AskUserQuestion")
            {
                HandleAskUserQuestionControl(requestId, toolUseId, input);
            }
            else
            {
                // Auto-approve other tool permission requests
                _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            }
        });
    }

    private void HandleExitPlanModeControl(string requestId, string toolUseId)
    {
        _exitPlanModeAutoCount++;

        if (AutoConfirmEnabled && _exitPlanModeAutoCount <= 2)
        {
            _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            Messages.Add(new MessageViewModel(MessageRole.System, "Plan approved automatically."));
        }
        else
        {
            if (AutoConfirmEnabled && _exitPlanModeAutoCount > 2)
            {
                AutoConfirmEnabled = false;
                Messages.Add(new MessageViewModel(MessageRole.System,
                    "Auto-confirm disabled (loop detected). Please confirm manually."));
            }

            // Store pending request for manual confirmation
            _pendingControlRequestId = requestId;
            _pendingControlToolUseId = toolUseId;

            var questionMsg = new MessageViewModel(MessageRole.System, "Exit plan mode?")
            {
                QuestionDisplay = new QuestionDisplayModel
                {
                    QuestionText = "Claude wants to exit plan mode and start implementing. Approve?",
                    Options =
                    [
                        new QuestionOption { Label = "Yes, go ahead", Description = "Approve plan and start implementation" },
                        new QuestionOption { Label = "No, keep planning", Description = "Stay in plan mode" }
                    ]
                }
            };
            Messages.Add(questionMsg);
            UpdateCta(CtaState.AnswerQuestion);
        }
    }

    private void HandleAskUserQuestionControl(string requestId, string toolUseId, JsonElement input)
    {
        _pendingControlRequestId = requestId;
        _pendingControlToolUseId = toolUseId;
        _pendingQuestionInput = input.ValueKind != JsonValueKind.Undefined ? input : null;
        _pendingQuestionAnswers.Clear();

        try
        {
            if (!input.TryGetProperty("questions", out var questionsArr)
                || questionsArr.ValueKind != JsonValueKind.Array)
                return;

            _pendingQuestionCount = questionsArr.GetArrayLength();

            foreach (var q in questionsArr.EnumerateArray())
            {
                var question = q.TryGetProperty("question", out var qText) ? qText.GetString() ?? "" : "";
                var options = new List<QuestionOption>();

                if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var opt in opts.EnumerateArray())
                    {
                        options.Add(new QuestionOption
                        {
                            Label = opt.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                            Description = opt.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""
                        });
                    }
                }

                if (options.Count > 0)
                {
                    var questionMsg = new MessageViewModel(MessageRole.System, question)
                    {
                        QuestionDisplay = new QuestionDisplayModel
                        {
                            QuestionText = question,
                            Options = options
                        }
                    };
                    Messages.Add(questionMsg);
                }
                else
                {
                    Messages.Add(new MessageViewModel(MessageRole.System, $"Claude asked: {question}"));
                }
            }

            UpdateCta(CtaState.AnswerQuestion);
        }
        catch (JsonException) { }
    }

    private void HandleControlAnswer(string answer)
    {
        var requestId = _pendingControlRequestId!;
        var toolUseId = _pendingControlToolUseId;

        // Clear question buttons from all messages (they've been answered)
        ClearQuestionDisplays();

        // ExitPlanMode — simple allow/deny
        if (_pendingQuestionInput is null)
        {
            _pendingControlRequestId = null;
            _pendingControlToolUseId = null;

            if (answer == "Yes, go ahead")
                _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            else
                _cliService.SendControlResponse(requestId, "deny", errorMessage: "User chose to keep planning");

            Messages.Add(new MessageViewModel(MessageRole.User, answer));
            UpdateCta(CtaState.Processing);
            return;
        }

        // AskUserQuestion — collect answers, then build updatedInput with questions+answers
        // Find the question text for this answer index
        try
        {
            var input = _pendingQuestionInput.Value;
            if (input.TryGetProperty("questions", out var questionsArr)
                && questionsArr.ValueKind == JsonValueKind.Array)
            {
                var idx = _pendingQuestionAnswers.Count;
                var questions = questionsArr.EnumerateArray().ToList();
                if (idx < questions.Count)
                {
                    var questionText = questions[idx].TryGetProperty("question", out var qt)
                        ? qt.GetString() ?? "" : "";
                    _pendingQuestionAnswers.Add((questionText, answer));
                }
            }
        }
        catch (JsonException) { }

        if (_pendingQuestionAnswers.Count >= _pendingQuestionCount)
        {
            // All answers collected — send control_response
            var answersDict = new Dictionary<string, string>();
            foreach (var (q, a) in _pendingQuestionAnswers)
                answersDict[q] = a;

            // Build updatedInput JSON: { "questions": [...original...], "answers": {...} }
            var questionsJson = "[]";
            try
            {
                if (_pendingQuestionInput?.TryGetProperty("questions", out var qa) == true)
                    questionsJson = qa.GetRawText();
            }
            catch { }

            var answersJson = JsonSerializer.Serialize(answersDict);
            var updatedInputJson = "{\"questions\":" + questionsJson + ",\"answers\":" + answersJson + "}";

            _cliService.SendControlResponse(requestId, "allow",
                updatedInputJson: updatedInputJson, toolUseId: toolUseId);

            // Show user's answers
            foreach (var (q, a) in _pendingQuestionAnswers)
                Messages.Add(new MessageViewModel(MessageRole.User, a));

            _pendingControlRequestId = null;
            _pendingControlToolUseId = null;
            _pendingQuestionInput = null;
            _pendingQuestionAnswers.Clear();
            _pendingQuestionCount = 0;
            UpdateCta(CtaState.Processing);
        }
    }

    private void ClearQuestionDisplays()
    {
        foreach (var msg in Messages)
        {
            if (msg.QuestionDisplay is not null)
                msg.QuestionDisplay = null;
        }
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

    /// <summary>
    /// Esc key handler: LIFO — pop last queued message back to input, or cancel Claude if queue is empty.
    /// Returns true if an action was taken.
    /// </summary>
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
        StatusText = "Ready";
        ContextUsageText = "";
        TodoProgressText = "";
        _contextWarningShown = false;
        _contextWindowSize = 0;

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
            // Normalize known full model IDs to short aliases
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

    public string? WorkingDirectory => _cliService.WorkingDirectory;

    private void RefreshGitStatus()
    {
        var (branch, dirtyCount, unpushedCount) = _gitService.GetStatus(WorkingDirectory);
        if (branch is null)
        {
            GitStatusText = "no git";
            return;
        }

        var parts = new List<string> { branch };

        if (dirtyCount > 0)
            parts.Add($"{dirtyCount} uncommitted");

        if (unpushedCount > 0)
            parts.Add($"{unpushedCount} unpushed");

        if (dirtyCount == 0 && unpushedCount == 0)
            parts.Add("clean");

        GitStatusText = string.Join(" | ", parts);
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
        StatusText = "Ready";

        Messages.Add(new MessageViewModel(MessageRole.System,
            $"Loaded chat from history. {(entry.SessionId is not null ? "Session restored — you can continue." : "No session to restore.")}"));
        UpdateCta(CtaState.WaitingForUser);
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
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(att.FilePath, UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();

        var image = new System.Windows.Controls.Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly
        };

        var mainWindow = Application.Current.MainWindow;
        var previewWindow = new Window
        {
            Title = att.FileName,
            Width = Math.Min(bitmap.PixelWidth + 40, 1200),
            Height = Math.Min(bitmap.PixelHeight + 60, 800),
            MinWidth = 300,
            MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = mainWindow,
            Background = (Brush)mainWindow!.FindResource("BackgroundBrush"),
            Content = new ScrollViewer
            {
                Content = image,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8)
            }
        };

        previewWindow.ShowDialog();
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
        var hasPassword = !string.IsNullOrEmpty(_settings.SshMasterPassword);
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
            lines.Add($"- SSH master password for servers that don't accept key auth: `{_settings.SshMasterPassword}`");
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

    /// <summary>
    /// Show project picker overlay if there are multiple known projects.
    /// Called from App.xaml.cs after startup dialog when starting a new session.
    /// </summary>
    public void ShowProjectPickerIfNeeded()
    {
        var projects = _projectRegistry.GetMostRecentProjects(10);
        if (projects.Count < 2) return;

        PickerProjects.Clear();
        foreach (var p in projects)
            PickerProjects.Add(p);

        ShowProjectPicker = true;
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

internal enum CtaState
{
    Welcome,
    Ready,
    Processing,
    WaitingForUser,
    AnswerQuestion,
    ConfirmOperation
}
