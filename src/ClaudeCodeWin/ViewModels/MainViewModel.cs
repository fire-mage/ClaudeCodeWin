using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly ClaudeCliService _cliService;
    private readonly NotificationService _notificationService;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly GitService _gitService;
    private readonly UpdateService _updateService;
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

    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            SetProperty(ref _isProcessing, value);
            OnPropertyChanged(nameof(CanSend));
        }
    }

    public bool CanSend => !IsProcessing;

    public bool HasAttachments => Attachments.Count > 0;

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

    public AsyncRelayCommand SendCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand NewSessionCommand { get; }
    public RelayCommand RemoveAttachmentCommand { get; }
    public RelayCommand SelectFolderCommand { get; }
    public RelayCommand OpenRecentFolderCommand { get; }
    public RelayCommand RemoveRecentFolderCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public MainViewModel(ClaudeCliService cliService, NotificationService notificationService,
        SettingsService settingsService, AppSettings settings, GitService gitService,
        UpdateService updateService)
    {
        _cliService = cliService;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _settings = settings;
        _gitService = gitService;
        _updateService = updateService;

        SendCommand = new AsyncRelayCommand(SendMessageAsync, () => CanSend);
        CancelCommand = new RelayCommand(CancelProcessing, () => IsProcessing);
        NewSessionCommand = new RelayCommand(StartNewSession);
        RemoveAttachmentCommand = new RelayCommand(p =>
        {
            if (p is FileAttachment att)
                Attachments.Remove(att);
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

        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));

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

        // Initialize recent folders from settings
        foreach (var folder in settings.RecentFolders)
            RecentFolders.Add(folder);

        ShowWelcome = string.IsNullOrEmpty(settings.WorkingDirectory);
        ProjectPath = settings.WorkingDirectory ?? "";

        // Restore session and git status if project was already set
        if (!string.IsNullOrEmpty(settings.WorkingDirectory))
        {
            RefreshGitStatus();

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

        var userMsg = new MessageViewModel(MessageRole.User, text);
        Messages.Add(userMsg);

        List<FileAttachment>? attachments = Attachments.Count > 0 ? [.. Attachments] : null;
        Attachments.Clear();

        InputText = string.Empty;
        IsProcessing = true;
        StatusText = "Processing...";

        // Auto-inject context snapshot on first message of a new session
        var finalPrompt = text;
        if (_cliService.SessionId is null && !string.IsNullOrEmpty(WorkingDirectory))
        {
            var snapshotPath = Path.Combine(WorkingDirectory, "CONTEXT_SNAPSHOT.md");
            if (File.Exists(snapshotPath))
            {
                var snapshot = File.ReadAllText(snapshotPath);
                finalPrompt = $"<context-snapshot>\n{snapshot}\n</context-snapshot>\n\n{text}";
                Messages.Add(new MessageViewModel(MessageRole.System, "Context injected: CONTEXT_SNAPSHOT.md"));
            }
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
        });
    }

    private void UpdateTokenUsageText()
    {
        if (_sessionTurnCount == 0)
        {
            TokenUsageText = "";
            return;
        }

        var text = $"In: {FormatTokenCount(_sessionInputTokens)} | Out: {FormatTokenCount(_sessionOutputTokens)}";

        if (_contextWindow > 0 && _lastInputTokens > 0)
        {
            var pct = (int)((long)_lastInputTokens * 100 / _contextWindow);
            text += $" | Ctx: {pct}%";
        }

        text += $" | Turns: {_sessionTurnCount}";
        TokenUsageText = text;
    }

    private static string FormatTokenCount(long tokens)
    {
        return tokens switch
        {
            >= 1_000_000 => $"{tokens / 1_000_000.0:F1}M",
            >= 1_000 => $"{tokens / 1_000.0:F1}K",
            _ => tokens.ToString()
        };
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
}
