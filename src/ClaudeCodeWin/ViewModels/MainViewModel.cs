using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        ## Important rules
        - Do NOT use the AskUserQuestion tool — it is not supported in this environment and will be auto-answered by the system without user input. When you need to ask the user clarifying questions, ask them directly as plain text in your response. Use numbered options if applicable.
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
    private VersionInfo? _pendingUpdate;
    private string? _downloadedUpdatePath;

    private string _inputText = string.Empty;
    private bool _isProcessing;
    private string _statusText = "Ready";
    private string _modelName = "";
    private MessageViewModel? _currentAssistantMessage;
    private bool _showWelcome;
    private bool _isFirstDelta;
    private string _projectPath = "";
    private string _gitStatusText = "";
    private string _tokenUsageText = "";
    private long _sessionInputTokens;
    private long _sessionOutputTokens;
    private int _sessionTurnCount;
    private int _contextWindow;
    private int _lastInputTokens;

    public ObservableCollection<MessageViewModel> Messages { get; } = [];
    public ObservableCollection<FileAttachment> Attachments { get; } = [];
    public ObservableCollection<string> RecentFolders { get; } = [];
    public ObservableCollection<QueuedMessage> MessageQueue { get; } = [];

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

    public bool HasAttachments => Attachments.Count > 0;
    public bool HasQueuedMessages => MessageQueue.Count > 0;

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
        set => SetProperty(ref _modelName, value);
    }

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

    public string TokenUsageText
    {
        get => _tokenUsageText;
        set => SetProperty(ref _tokenUsageText, value);
    }

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
    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public MainViewModel(ClaudeCliService cliService, NotificationService notificationService,
        SettingsService settingsService, AppSettings settings, GitService gitService,
        UpdateService updateService, FileIndexService fileIndexService)
    {
        _cliService = cliService;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _settings = settings;
        _gitService = gitService;
        _updateService = updateService;
        _fileIndexService = fileIndexService;

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
            await _updateService.CheckForUpdateAsync();
            if (_pendingUpdate is null)
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
                _ = SendDirectAsync(qm.Text, null);
            }
        });
        ReturnQueuedToInputCommand = new RelayCommand(p =>
        {
            if (p is QueuedMessage qm)
            {
                MessageQueue.Remove(qm);
                InputText = qm.Text;
            }
        });

        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));
        MessageQueue.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQueuedMessages));

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
                    _ = _updateService.DownloadAndApplyAsync(info);
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
                StatusText = "Ready";
                MessageBox.Show(error, "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        };

        // Start periodic update checks
        _updateService.StartPeriodicCheck();

        _cliService.OnTextDelta += HandleTextDelta;
        _cliService.OnToolUseStarted += HandleToolUseStarted;
        _cliService.OnCompleted += HandleCompleted;
        _cliService.OnError += HandleError;
        _cliService.OnAskUserQuestion += HandleAskUserQuestion;

        // Initialize recent folders from settings
        foreach (var folder in settings.RecentFolders)
            RecentFolders.Add(folder);

        ShowWelcome = string.IsNullOrEmpty(settings.WorkingDirectory);
        ProjectPath = settings.WorkingDirectory ?? "";

        // Restore session and git status if project was already set
        if (!string.IsNullOrEmpty(settings.WorkingDirectory))
        {
            RefreshGitStatus();
            _ = Task.Run(() => _fileIndexService.BuildIndex(settings.WorkingDirectory));

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
        RefreshGitStatus();

        // Rebuild file index in background
        _ = Task.Run(() => _fileIndexService.BuildIndex(folder));

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

        // If Claude is busy, queue the message
        if (IsProcessing)
        {
            MessageQueue.Add(new QueuedMessage(text));
            InputText = string.Empty;
            return;
        }

        await SendDirectAsync(text, Attachments.Count > 0 ? [.. Attachments] : null);
    }

    private async Task SendDirectAsync(string text, List<FileAttachment>? attachments)
    {
        var userMsg = new MessageViewModel(MessageRole.User, text);
        Messages.Add(userMsg);

        if (attachments is not null)
            Attachments.Clear();

        InputText = string.Empty;
        IsProcessing = true;
        StatusText = "Processing...";

        // Auto-inject system instruction and context snapshot on first message of a new session
        var finalPrompt = text;
        if (_cliService.SessionId is null)
        {
            var preamble = SystemInstruction;

            if (!string.IsNullOrEmpty(WorkingDirectory))
            {
                var snapshotPath = Path.Combine(WorkingDirectory, "CONTEXT_SNAPSHOT.md");
                if (File.Exists(snapshotPath))
                {
                    var snapshot = File.ReadAllText(snapshotPath);
                    preamble += $"\n\n<context-snapshot>\n{snapshot}\n</context-snapshot>";
                    Messages.Add(new MessageViewModel(MessageRole.System, "Context injected: CONTEXT_SNAPSHOT.md"));
                }
            }

            finalPrompt = $"{preamble}\n\n{text}";
        }

        _currentAssistantMessage = new MessageViewModel(MessageRole.Assistant) { IsStreaming = true, IsThinking = true };
        _isFirstDelta = true;
        Messages.Add(_currentAssistantMessage);

        await _cliService.SendMessageAsync(finalPrompt, attachments);
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

    private void HandleToolUseStarted(string toolName, string input)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_currentAssistantMessage is not null)
            {
                if (_isFirstDelta)
                {
                    _isFirstDelta = false;
                    _currentAssistantMessage.IsThinking = false;
                }
            }
            _currentAssistantMessage?.ToolUses.Add(new ToolUseViewModel(toolName, input));
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
            IsProcessing = false;
            StatusText = "Ready";

            if (!string.IsNullOrEmpty(result.Model))
                ModelName = result.Model;

            // Accumulate token usage
            _sessionInputTokens += result.InputTokens + result.CacheReadTokens + result.CacheCreationTokens;
            _sessionOutputTokens += result.OutputTokens;
            _sessionTurnCount++;
            _lastInputTokens = result.InputTokens + result.CacheReadTokens + result.CacheCreationTokens;
            if (result.ContextWindow > 0)
                _contextWindow = result.ContextWindow;
            UpdateTokenUsageText();

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

            // Auto-send next queued message
            if (MessageQueue.Count > 0)
            {
                var next = MessageQueue[0];
                MessageQueue.RemoveAt(0);
                _ = SendDirectAsync(next.Text, null);
            }
        });
    }

    private void UpdateTokenUsageText()
    {
        if (_contextWindow > 0 && _lastInputTokens > 0)
        {
            var pct = (int)((long)_lastInputTokens * 100 / _contextWindow);
            TokenUsageText = $"Memory used: {pct}%";
        }
        else
        {
            TokenUsageText = "";
        }
    }

    private void HandleAskUserQuestion(string rawJson)
    {
        // AskUserQuestion cannot work interactively in pipe mode (-p):
        // stdin is closed after sending the prompt, so the CLI auto-selects answers.
        // We show the question text as an informational system message.
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("questions", out var questionsArr)
                    || questionsArr.ValueKind != JsonValueKind.Array)
                    return;

                var parts = new List<string>();
                foreach (var q in questionsArr.EnumerateArray())
                {
                    var question = q.TryGetProperty("question", out var qText) ? qText.GetString() ?? "" : "";
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Claude asked: {question}");

                    if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var opt in opts.EnumerateArray())
                        {
                            var label = opt.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                            var desc = opt.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                            sb.AppendLine($"  - {label}: {desc}");
                        }
                    }

                    sb.Append("(Auto-answered by CLI — pipe mode limitation)");
                    parts.Add(sb.ToString());
                }

                if (parts.Count > 0)
                    Messages.Add(new MessageViewModel(MessageRole.System, string.Join("\n\n", parts)));
            }
            catch (JsonException)
            {
                // Invalid JSON — ignore
            }
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

        Messages.Clear();
        MessageQueue.Clear();
        _cliService.ResetSession();
        ModelName = "";
        StatusText = "Ready";
        _sessionInputTokens = 0;
        _sessionOutputTokens = 0;
        _sessionTurnCount = 0;
        _contextWindow = 0;
        _lastInputTokens = 0;
        UpdateTokenUsageText();

        // Clear saved session for current project
        if (!string.IsNullOrEmpty(WorkingDirectory)
            && _settings.SavedSessions.Remove(WorkingDirectory))
        {
            _settingsService.Save(_settings);
        }
    }

    public string? WorkingDirectory => _cliService.WorkingDirectory;

    private void RefreshGitStatus()
    {
        var (branch, dirtyCount) = _gitService.GetStatus(WorkingDirectory);
        if (branch is null)
        {
            GitStatusText = "";
            return;
        }
        GitStatusText = dirtyCount > 0 ? $"{branch} | {dirtyCount} dirty" : branch;
    }

    public void AddAttachment(FileAttachment attachment)
    {
        if (Attachments.All(a => a.FilePath != attachment.FilePath))
            Attachments.Add(attachment);
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
}
