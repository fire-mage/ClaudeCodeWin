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
        - **AskUserQuestion support**: When you use the AskUserQuestion tool, the user sees interactive buttons and can select an option. The selected answer is sent back to you as the next user message.

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
    private string? _currentChatId;

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
    public RelayCommand ViewChangedFileCommand { get; }
    public RelayCommand AnswerQuestionCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public MainViewModel(ClaudeCliService cliService, NotificationService notificationService,
        SettingsService settingsService, AppSettings settings, GitService gitService,
        UpdateService updateService, FileIndexService fileIndexService,
        ChatHistoryService chatHistoryService)
    {
        _cliService = cliService;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _settings = settings;
        _gitService = gitService;
        _updateService = updateService;
        _fileIndexService = fileIndexService;
        _chatHistoryService = chatHistoryService;

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
        ViewChangedFileCommand = new RelayCommand(p =>
        {
            if (p is string filePath)
                ShowFileDiff(filePath);
        });
        AnswerQuestionCommand = new RelayCommand(p =>
        {
            if (p is string answer)
                _ = SendDirectAsync(answer, null);
        });

        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));
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
        _cliService.OnToolResult += HandleToolResult;
        _cliService.OnCompleted += HandleCompleted;
        _cliService.OnError += HandleError;
        _cliService.OnAskUserQuestion += HandleAskUserQuestion;
        _cliService.OnFileChanged += HandleFileChanged;

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

        ChangedFiles.Clear();
        _cliService.ClearFileSnapshots();
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

        // Send via persistent process (starts process if needed)
        await Task.Run(() => _cliService.SendMessage(finalPrompt, attachments));
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
            }
        });
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
            SaveChatHistory();

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
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("questions", out var questionsArr)
                    || questionsArr.ValueKind != JsonValueKind.Array)
                    return;

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
            }
            catch (JsonException) { }
        });
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
        _sessionInputTokens = 0;
        _sessionOutputTokens = 0;
        _sessionTurnCount = 0;
        _contextWindow = 0;
        _lastInputTokens = 0;
        UpdateTokenUsageText();

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
            _ = Task.Run(() => _fileIndexService.BuildIndex(entry.ProjectPath));
        }

        ShowWelcome = false;
        ModelName = "";
        StatusText = "Ready";

        Messages.Add(new MessageViewModel(MessageRole.System,
            $"Loaded chat from history. {(entry.SessionId is not null ? "Session restored — you can continue." : "No session to restore.")}"));
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
}
