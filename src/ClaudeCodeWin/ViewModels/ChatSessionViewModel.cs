using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using ClaudeCodeWin.ContextSnapshot;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

/// <summary>
/// Container for shared services passed to each ChatSessionViewModel.
/// These services are project-level singletons shared across all chat sessions.
/// </summary>
internal class ChatSessionServices
{
    public required NotificationService Notification { get; init; }
    public required SettingsService SettingsService { get; init; }
    public required AppSettings Settings { get; init; }
    public required GitService Git { get; init; }
    public required ChatHistoryService ChatHistory { get; init; }
    public required UsageService Usage { get; init; }
    public required BacklogService Backlog { get; init; }
    public required ProjectRegistryService ProjectRegistry { get; init; }
    public required ContextSnapshotService ContextSnapshot { get; init; }
    public DevKbService? DevKb { get; init; }
}

/// <summary>
/// Per-chat session state and logic. Each chat sub-tab owns one instance.
/// Manages its own ClaudeCliService process, messages, composer, review, nudge,
/// control requests, and background tasks independently of other chat sessions.
/// </summary>
public class ChatSessionViewModel : ViewModelBase
{
    // ─── Parent and Services ───
    private readonly MainViewModel _parent;
    internal readonly ChatSessionServices Services;
    internal readonly ClaudeCliService CliService;
    private readonly ChatMessageAssembler _messageAssembler;

    // ─── Per-chat state fields ───
    private bool _isProcessing;
    private bool _isReviewInProgress;
    private string _statusText = "";
    private string _modelName = "";
    private bool _hasResponseStarted;
    private string? _lastSentText;
    private List<FileAttachment>? _lastSentAttachments;
    private string _effectiveProjectName = "";
    private string _contextUsageText = "";
    private string _contextPctText = "";
    private string? _currentChatId;
    private string _ctaText = "";
    private CtaState _ctaState = CtaState.Ready;
    private int _contextWindowSize;
    private bool _contextWarningShown;
    private int _previousInputTokens;
    private int _previousCtxPercent;
    private string _todoProgressText = "";
    private bool _showRateLimitBanner;
    private string _rateLimitCountdown = "";
    private int _sendGeneration;
    private int _activeSendGeneration;
    private int _crashRetryCount;
    private const int MaxCrashRetries = 1;
    private bool _needsPreambleInjection = true;
    private bool _apiKeyExpiryChecked;
    private int _exitPlanModeAutoCount;
    private HashSet<string> _preTurnDirtyFiles = [];

    // Control request protocol state
    private int _pendingQuestionCount;
    private string? _pendingControlRequestId;
    private string? _pendingControlToolUseId;
    private JsonElement? _pendingQuestionInput;
    private readonly List<(string question, string answer)> _pendingQuestionAnswers = [];
    private readonly List<MessageViewModel> _pendingQuestionMessages = [];

    // Review state
    private static readonly HashSet<string> TextDocExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".mdx", ".rst", ".adoc", ".log", ".csv" };
    private static readonly HashSet<string> MediaBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".svg", ".tiff",
        ".mp3", ".mp4", ".wav", ".avi", ".mkv", ".mov", ".flac", ".ogg", ".webm",
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".zip", ".tar", ".gz", ".7z", ".rar",
        ".exe", ".dll", ".so", ".dylib", ".o", ".obj", ".class", ".pyc", ".pdb",
        ".db", ".sqlite", ".sqlite3", ".lock"
    };
    private static readonly HashSet<string> NonReviewableFileNames = new(StringComparer.OrdinalIgnoreCase)
    { "LICENSE", "LICENCE", "CHANGELOG", "AUTHORS" };
    private static readonly HashSet<string> NonReviewableFullNames = new(StringComparer.OrdinalIgnoreCase)
    { "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "composer.lock",
      "Cargo.lock", "Pipfile.lock", "poetry.lock", "Gemfile.lock" };

    private ReviewService? _reviewService;
    private int _reviewAttempt;
    private bool _isAutoReviewPending;
    private bool _reviewCycleCompleted;
    private MessageViewModel? _currentReviewerMessage;
    private string? _lastReviewCriticalSnippet;
    private int _currentTaskStartIndex;
    private DispatcherTimer? _reviewStatusClearTimer;
    private DispatcherTimer? _reviewTimeoutTimer;
    private DispatcherTimer? _reviewNudgeTimer;
    private string _reviewStatusText = "";

    // Nudge state
    private const int NudgeInactivitySeconds = 300;
    private const int MaxNudgesPerTurn = 3;
    private const string NudgeMessageText =
        "It seems like you might be stuck. Please check your current state and continue, " +
        "or let me know what's blocking you.";
    private DispatcherTimer? _nudgeTimer;
    private DateTime _lastActivityTime;
    private int _nudgeCount;
    private bool _showNudgeButton;

    // Usage event handlers (stored for unsubscribe in Dispose)
    private Action<bool>? _rateLimitChangedHandler;
    private Action? _usageUpdatedHandler;

    // Background tasks state
    private DispatcherTimer? _bgTaskTimer;
    private bool _hasBackgroundTasks;
    private string _backgroundTasksHeaderText = "";

    // ─── Observable Collections ───
    public ObservableCollection<MessageViewModel> Messages { get; } = [];
    public ObservableCollection<FileAttachment> Attachments { get; } = [];
    public ObservableCollection<QueuedMessage> MessageQueue { get; } = [];
    public ObservableCollection<string> ChangedFiles { get; } = [];
    public ObservableCollection<ComposerBlock> ComposerBlocks { get; } = [new TextComposerBlock()];
    public ObservableCollection<BackgroundTaskViewModel> BackgroundTasks { get; } = [];

    // ─── Sub-tab linkage ───
    public SubTab? LinkedSubTab { get; set; }
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!SetProperty(ref _isActive, value)) return;
            if (value && LinkedSubTab != null)
                LinkedSubTab.NeedsAttention = false;
        }
    }

    // ─── Properties ───

    public string? WorkingDirectory => _parent.WorkingDirectory;

    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (SetProperty(ref _isProcessing, value) && LinkedSubTab != null)
                LinkedSubTab.IsWorking = value;
        }
    }

    public bool IsReviewInProgress
    {
        get => _isReviewInProgress;
        set => SetProperty(ref _isReviewInProgress, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ReviewStatusText
    {
        get => _reviewStatusText;
        set => SetProperty(ref _reviewStatusText, value);
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

    public string EffectiveProjectName
    {
        get => _effectiveProjectName;
        set => SetProperty(ref _effectiveProjectName, value);
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

    public bool ShowNudgeButton
    {
        get => _showNudgeButton;
        set => SetProperty(ref _showNudgeButton, value);
    }

    public bool HasBackgroundTasks
    {
        get => _hasBackgroundTasks;
        private set => SetProperty(ref _hasBackgroundTasks, value);
    }

    public string BackgroundTasksHeaderText
    {
        get => _backgroundTasksHeaderText;
        private set => SetProperty(ref _backgroundTasksHeaderText, value);
    }

    public string InputText
    {
        get => string.Join("", ComposerBlocks.OfType<TextComposerBlock>().Select(t => t.Text));
        set
        {
            ClearComposerText();
            if (ComposerBlocks[0] is TextComposerBlock tb)
                tb.Text = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsComposerEmpty));
        }
    }

    public bool IsComposerEmpty =>
        ComposerBlocks.Count == 1
        && ComposerBlocks[0] is TextComposerBlock tb
        && string.IsNullOrWhiteSpace(tb.Text);

    public void NotifyComposerChanged() => OnPropertyChanged(nameof(IsComposerEmpty));

    public bool HasAttachments => Attachments.Count > 0;
    public bool HasQueuedMessages => MessageQueue.Count > 0;
    public bool HasChangedFiles => ChangedFiles.Count > 0;
    public string ChangedFilesText => $"{ChangedFiles.Count} file(s) changed";
    public bool HasDialogHistory => Messages.Any(m => m.Role == MessageRole.Assistant);

    public FinalizeActionsViewModel FinalizeActions { get; }

    // ─── Constructor ───

    internal ChatSessionViewModel(ClaudeCliService cliService, MainViewModel parent, ChatSessionServices services)
    {
        _parent = parent;
        Services = services;
        CliService = cliService;
        _messageAssembler = new ChatMessageAssembler(Messages);

        FinalizeActions = new FinalizeActionsViewModel(services.SettingsService, services.Settings, () => WorkingDirectory);
        FinalizeActions.OnCommitRequested += msg => _ = SendDirectAsync(msg, null);
        FinalizeActions.OnRunTaskRequested += task =>
        {
            if (_parent.OwnerWindow is not null)
                TaskRunnerService.RunTaskPublic(task, _parent, _parent.OwnerWindow);
        };

        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDialogHistory));
        MessageQueue.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQueuedMessages));
        ChangedFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasChangedFiles));
            OnPropertyChanged(nameof(ChangedFilesText));
        };

        WireCliEvents();
        InitializeNudge();
        InitializeBackgroundTaskTimer();
        UpdateCta(CtaState.Ready);
    }

    // ─── Dispose ───

    public void Dispose()
    {
        CancelReview();
        CliService.StopSession();
        StopNudgeTimer();
        _bgTaskTimer?.Stop();
        FinalizeActions.StopTaskSuggestionTimer();

        // Unsubscribe from shared UsageService to prevent leaks
        if (_rateLimitChangedHandler != null)
            Services.Usage.OnRateLimitChanged -= _rateLimitChangedHandler;
        if (_usageUpdatedHandler != null)
            Services.Usage.OnUsageUpdated -= _usageUpdatedHandler;

        _messageAssembler.DisposeAllMessages();
    }

    // ─── CLI Event Wiring ───

    private void WireCliEvents()
    {
        CliService.OnTextBlockStart += HandleTextBlockStart;
        CliService.OnTextDelta += HandleTextDelta;
        CliService.OnThinkingDelta += HandleThinkingDelta;
        CliService.OnToolUseStarted += HandleToolUseStarted;
        CliService.OnToolResult += HandleToolResult;
        CliService.OnCompleted += HandleCompleted;
        CliService.OnError += HandleError;
        CliService.OnControlRequest += HandleControlRequest;
        CliService.OnFileChanged += HandleFileChanged;
        CliService.OnMessageStarted += HandleMessageStarted;
        CliService.OnRateLimitDetected += () =>
            RunOnUI(() => Services.Usage.SetRateLimitedExternally());

        _rateLimitChangedHandler = isLimited =>
        {
            RunOnUI(() =>
            {
                if (isLimited)
                {
                    ShowRateLimitBanner = true;
                    RateLimitCountdown = Services.Usage.GetSessionCountdown();
                    // Only add message to the active chat to avoid duplicates across tabs
                    if (IsActive)
                        Messages.Add(new MessageViewModel(MessageRole.System,
                            $"Rate limit reached. Resets in {RateLimitCountdown}."));
                }
                else
                {
                    ShowRateLimitBanner = false;
                    RateLimitCountdown = "";
                    if (IsActive)
                        Messages.Add(new MessageViewModel(MessageRole.System,
                            "Rate limit cleared. You can continue working."));
                }
            });
        };
        Services.Usage.OnRateLimitChanged += _rateLimitChangedHandler;

        _usageUpdatedHandler = () =>
        {
            if (_showRateLimitBanner)
                RateLimitCountdown = Services.Usage.GetSessionCountdown();
        };
        Services.Usage.OnUsageUpdated += _usageUpdatedHandler;

        CliService.OnCompactionDetected += msg =>
        {
            RunOnUI(() =>
            {
                var ctx = ContextUsageText;
                Messages.Add(new MessageViewModel(MessageRole.System,
                    $"Context auto-compacted. {msg} [{ctx}]"));
                DiagnosticLogger.Log("COMPACTION", $"{msg} ctx={ctx}");
                _contextWarningShown = false;
            });
        };

        CliService.OnSystemNotification += msg =>
        {
            DiagnosticLogger.Log("SYSTEM_NOTIFICATION", msg);
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MESSAGING
    // ═══════════════════════════════════════════════════════════════════

    public async Task SendMessageAsync()
    {
        var (text, inlineAttachments, contentParts) = BuildComposerContent();
        if (string.IsNullOrEmpty(text))
            return;

        List<FileAttachment>? barAttachments = Attachments.Count > 0 ? [.. Attachments] : null;
        List<FileAttachment>? allAttachments = barAttachments;
        if (inlineAttachments != null)
            allAttachments = [.. (allAttachments ?? []), .. inlineAttachments];

        if (IsProcessing)
        {
            MessageQueue.Add(new QueuedMessage(text, allAttachments));
            ClearComposer();
            return;
        }

        if (IsReviewInProgress)
        {
            Messages.Add(new MessageViewModel(MessageRole.System,
                "Cannot send messages during review. Press Escape to cancel review."));
            return;
        }

        _exitPlanModeAutoCount = 0;

        if (_pendingControlRequestId is not null)
        {
            var answerText = string.Join("", ComposerBlocks.OfType<TextComposerBlock>().Select(t => t.Text)).Trim();
            if (string.IsNullOrEmpty(answerText))
            {
                Messages.Add(new MessageViewModel(MessageRole.System, "Please type a text answer."));
                return;
            }
            ClearComposer();
            HandleControlAnswer(answerText);
            return;
        }

        _currentTaskStartIndex = Messages.Count;
        _reviewCycleCompleted = false;
        ChangedFiles.Clear();
        ClearComposer();

        await SendDirectAsync(text, allAttachments, contentParts: contentParts);
    }

    internal async Task SendDirectAsync(string text, List<FileAttachment>? attachments,
        string? participantLabel = null, List<MessageContentPart>? contentParts = null, bool skipUserMessage = false)
    {
        if (_reviewService?.IsActive == true && participantLabel is null)
        {
            CancelReview();
            Messages.Add(new MessageViewModel(MessageRole.System, "Review cancelled (user message)."));
        }

        if (!skipUserMessage)
        {
            var userMsg = new MessageViewModel(MessageRole.User, text);
            if (participantLabel is not null)
                userMsg.ReviewerLabel = participantLabel;
            if (attachments is not null)
                userMsg.Attachments = [.. attachments];
            if (contentParts is not null)
                userMsg.ContentParts = contentParts;
            Messages.Add(userMsg);
        }

        _lastSentText = text;
        _lastSentAttachments = attachments;
        _hasResponseStarted = false;

        EffectiveProjectName = "";
        if (participantLabel is null && !skipUserMessage)
            _crashRetryCount = 0;
        CliService.ClearFileSnapshots();
        _activeSendGeneration = ++_sendGeneration;
        IsProcessing = true;
        StatusText = "Processing...";
        StartNudgeTimer();
        UpdateCta(CtaState.Processing);

        var finalPrompt = text;
        if (_needsPreambleInjection)
        {
            _needsPreambleInjection = false;

            var preamble = MainViewModel.GetSystemInstructionText();

            var devKbSection = Services.DevKb?.BuildRequiredArticlesSection();
            if (!string.IsNullOrEmpty(devKbSection))
                preamble += devKbSection;

            // Inject workspace description when multi-project workspace is active
            if (_parent.ActiveWorkspace is { } ws && ws.Projects.Count > 0)
            {
                var escapedName = SecurityElement.Escape(ws.Name) ?? ws.Name;
                var wsLines = new List<string> { $"<workspace name=\"{escapedName}\">" };
                foreach (var proj in ws.Projects)
                {
                    var projInfo = Services.ProjectRegistry.GetProject(proj.Path);
                    var tech = projInfo?.TechStack ?? "";
                    var role = !string.IsNullOrEmpty(proj.Role) ? $" ({SecurityElement.Escape(proj.Role)})" : "";
                    var isPrimary = string.Equals(proj.Path, ws.PrimaryProjectPath, StringComparison.OrdinalIgnoreCase);
                    var prefix = isPrimary ? "Primary (CWD)" : "Related";
                    var name = Path.GetFileName(proj.Path);
                    wsLines.Add($"  {prefix}: {name} — {proj.Path}{role}{(tech.Length > 0 ? $" [{tech}]" : "")}");
                }
                wsLines.Add("</workspace>");
                preamble += "\n\n" + string.Join("\n", wsLines);
            }

            if (Services.Settings.ContextSnapshotEnabled)
            {
                await Services.ContextSnapshot.WaitForGenerationAsync(10000);
                var allPaths = _parent.GetAllProjectPaths();
                var snapshotPaths = allPaths.Count > 0
                    ? allPaths
                    : Services.ProjectRegistry.GetMostRecentProjects(1).Select(p => p.Path).ToList();
                var (combined, snapshotCount) = Services.ContextSnapshot.GetCombinedSnapshot(snapshotPaths);
                if (!string.IsNullOrEmpty(combined))
                {
                    preamble += $"\n\n<context-snapshot>\n{combined}\n</context-snapshot>";
                    Messages.Add(new MessageViewModel(MessageRole.System,
                        $"Context snapshot injected ({snapshotCount} projects)"));
                }
            }

            var registrySummary = Services.ProjectRegistry.BuildRegistrySummary();
            if (!string.IsNullOrEmpty(registrySummary))
                preamble += $"\n\n<project-registry>\n{registrySummary}\n</project-registry>";

            var sshInfo = BuildSshInfo();
            if (!string.IsNullOrEmpty(sshInfo))
                preamble += $"\n\n<ssh-access>\n{sshInfo}\n</ssh-access>";

            var apiKeyWarnings = new List<string>();
            foreach (var key in Services.Settings.ApiKeys)
            {
                var (days, isExpired, _) = key.GetExpiryStatus();
                if (isExpired)
                    apiKeyWarnings.Add($"{key.ServiceName} (expired {-days}d ago)");
            }
            if (apiKeyWarnings.Count > 0)
                preamble += $"\n\n<expired-api-keys>The following API keys are expired and should NOT be used: {string.Join(", ", apiKeyWarnings)}. Ask the user to update them in Settings > API Keys.</expired-api-keys>";

            finalPrompt = $"{preamble}\n\n{text}";
            CheckApiKeyExpiry();
        }

        var wd = WorkingDirectory;
        if (!string.IsNullOrEmpty(wd))
        {
            try
            {
                var dirtyBefore = await Task.Run(() => Services.Git.GetChangedFiles(wd));
                _preTurnDirtyFiles = new HashSet<string>(dirtyBefore, StringComparer.OrdinalIgnoreCase);
            }
            catch { _preTurnDirtyFiles = []; }
        }
        else
        {
            _preTurnDirtyFiles = [];
        }

        _messageAssembler.BeginAssistantMessage();
        await Task.Run(() => CliService.SendMessage(finalPrompt, attachments));
    }

    private void HandleTextBlockStart()
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;
            ResetNudgeActivity();
            _messageAssembler.HandleTextBlockStart();
        });
    }

    private void HandleTextDelta(string text)
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;
            ResetNudgeActivity();
            _messageAssembler.HandleTextDelta(text);
            _hasResponseStarted = _messageAssembler.CurrentMessage is not null;
        });
    }

    private void HandleThinkingDelta(string text)
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;
            ResetNudgeActivity();
            _messageAssembler.HandleThinkingDelta(text);
        });
    }

    private void HandleToolUseStarted(string toolName, string toolUseId, string input)
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;
            ResetNudgeActivity();
            _messageAssembler.HandleToolUseStarted(toolName, toolUseId, input);
            _hasResponseStarted = _messageAssembler.CurrentMessage is not null;

            if (toolName == "TodoWrite")
                UpdateTodoProgress(input);
            _parent.TryRegisterProjectFromToolUse(toolName, input);
            UpdateEffectiveProject(toolName, input);
            TryTrackBackgroundTask(toolName, toolUseId, input);
        });
    }

    private void HandleToolResult(string toolName, string toolUseId, string content)
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;
            ResetNudgeActivity();
            _messageAssembler.HandleToolResult(toolName, toolUseId, content);
            TryUpdateBackgroundTask(toolName, toolUseId, content);
        });
    }

    private void HandleMessageStarted(string model, int inputTokens, int cacheReadTokens, int cacheCreationTokens)
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;
            ResetNudgeActivity();
            if (!string.IsNullOrEmpty(model) && string.IsNullOrEmpty(ModelName))
                ModelName = model;

            if (_contextWindowSize > 0 && inputTokens > 0)
            {
                var totalInput = inputTokens + cacheReadTokens + cacheCreationTokens;
                var pct = (int)(totalInput * 100.0 / _contextWindowSize);
                ContextPctText = $"{pct}%";
            }
        });
    }

    private void HandleCompleted(ResultData result)
    {
        RunOnUI(async () =>
        {
            if (_activeSendGeneration != _sendGeneration) return;

            List<FeatureProposalDetector.FeatureProposal>? teamProposals = null;
            var currentMsg = _messageAssembler.CurrentMessage;
            if (currentMsg is not null && !string.IsNullOrEmpty(WorkingDirectory))
            {
                var (cleaned, proposals) = FeatureProposalDetector.Extract(currentMsg.Text);
                if (proposals.Count > 0)
                {
                    currentMsg.Text = cleaned;
                    teamProposals = proposals;
                }
            }

            _messageAssembler.HandleCompleted();

            if (teamProposals is not null)
            {
                var wd = WorkingDirectory!;
                var total = teamProposals.Count;
                var backlog = Services.Backlog;
                var planner = _parent.Planner;
                var team = _parent.Team;
                _ = Task.Run(() =>
                {
                    var succeeded = 0;
                    foreach (var p in teamProposals)
                    {
                        try
                        {
                            var feature = backlog.AddFeature(wd, p.RawIdea);
                            if (p.Priority != 100)
                                backlog.ModifyFeature(feature.Id, f => f.Priority = p.Priority);
                            planner?.StartPlanning(feature);
                            succeeded++;
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLogger.Log("TEAM_TASK_ERROR", $"Failed to add task: {ex.Message}");
                        }
                    }
                    return succeeded;
                }).ContinueWith(t => RunOnUI(() =>
                {
                    var ok = t.IsFaulted ? 0 : t.Result;
                    if (ok > 0) team?.Refresh();
                    var msg = ok == total
                        ? $"{ok} task(s) sent to Team pipeline"
                        : ok > 0
                            ? $"{ok}/{total} task(s) sent to Team pipeline ({total - ok} failed)"
                            : "Failed to send tasks to Team";
                    Messages.Add(new MessageViewModel(MessageRole.System, msg));
                }), TaskScheduler.Default);
            }

            IsProcessing = false;
            StopNudgeTimer();
            _messageAssembler.ClearAllThinking();
            StatusText = "";
            _crashRetryCount = 0;
            UpdateCta(CtaState.WaitingForUser);

            if (_parent.TechnicalWriter is not null && currentMsg is not null
                && !string.IsNullOrEmpty(currentMsg.Text) && !string.IsNullOrEmpty(WorkingDirectory))
                _parent.TechnicalWriter.AccumulateText(WorkingDirectory, currentMsg.Text);

            if (!string.IsNullOrEmpty(result.Model))
                ModelName = result.Model;

            if (result.ContextWindow > 0)
                _contextWindowSize = result.ContextWindow;

            var lastCallInput = result.LastCallInputTokens + result.LastCallCacheReadTokens + result.LastCallCacheCreationTokens;
            var lastCallTotal = lastCallInput + result.LastCallOutputTokens;
            var usePerCall = lastCallInput > 0;
            var aggInput = result.InputTokens + result.CacheReadTokens + result.CacheCreationTokens;
            var aggTotal = aggInput + result.OutputTokens;
            var totalTokens = usePerCall ? lastCallTotal : aggTotal;
            var totalInput = usePerCall ? lastCallInput : aggInput;

            if (_contextWindowSize > 0 && totalInput > 0)
            {
                var pct = (int)(totalTokens * 100.0 / _contextWindowSize);
                if (!usePerCall && pct > 100) pct = Math.Min(pct, 99);

                ContextUsageText = $"Ctx: {pct}%";
                ContextPctText = $"{pct}%";

                DiagnosticLogger.Log("CTX",
                    $"source={(usePerCall ? "per-call" : "aggregated")} " +
                    $"perCall: input={result.LastCallInputTokens:N0} cache_read={result.LastCallCacheReadTokens:N0} " +
                    $"cache_create={result.LastCallCacheCreationTokens:N0} output={result.LastCallOutputTokens:N0} " +
                    $"agg: input={result.InputTokens:N0} cache_read={result.CacheReadTokens:N0} " +
                    $"cache_create={result.CacheCreationTokens:N0} output={result.OutputTokens:N0} " +
                    $"used={totalTokens:N0} window={_contextWindowSize:N0} pct={pct}%");

                if (_previousCtxPercent > 0 && _previousCtxPercent - pct > 20)
                {
                    var compMsg = $"Context compacted: {_previousCtxPercent}% \u2192 {pct}% " +
                                  $"({_previousInputTokens:N0} \u2192 {totalInput:N0} input tokens)";
                    Messages.Add(new MessageViewModel(MessageRole.System, compMsg));
                    DiagnosticLogger.Log("COMPACTION_DETECTED", compMsg);
                    _contextWarningShown = false;
                    _needsPreambleInjection = true;
                    ResetTaskOutputSentFlags();
                }

                _previousInputTokens = totalInput;
                _previousCtxPercent = pct;

                if (pct >= 80 && !_contextWarningShown)
                {
                    _contextWarningShown = true;
                    Messages.Add(new MessageViewModel(MessageRole.System,
                        $"Context is {pct}% full ({totalTokens:N0}/{_contextWindowSize:N0} tokens). Consider starting a new session."));
                }
            }

            // Only save session ID from the active chat to avoid clobbering between tabs
            if (IsActive && !string.IsNullOrEmpty(result.SessionId) && !string.IsNullOrEmpty(WorkingDirectory))
            {
                Services.Settings.SavedSessions[WorkingDirectory] = new SavedSession
                {
                    SessionId = result.SessionId,
                    CreatedAt = DateTime.Now
                };
                Services.SettingsService.Save(Services.Settings);
            }

            _parent.RefreshGitStatusPublic();
            await DetectChangedFilesFromGitAsync();
            SaveChatHistory();

            Services.Notification.NotifyIfInactive();

            if (!IsActive && LinkedSubTab != null)
                LinkedSubTab.NeedsAttention = true;

            if (MessageQueue.Count > 0)
            {
                var next = MessageQueue[0];
                MessageQueue.RemoveAt(0);
                _ = SendDirectAsync(next.Text, next.Attachments);
            }
            else
            {
                _exitPlanModeAutoCount = 0;
                OnTurnCompleted();

                if (_parent.IsTeamPausedForConflict && !IsReviewInProgress)
                    _parent.ResumeTeamAfterConflictPublic();
            }
        });
    }

    private void HandleError(string error)
    {
        RunOnUI(() =>
        {
            if (_activeSendGeneration != _sendGeneration) return;

            if (error.Contains("exited unexpectedly") && _lastSentText is not null
                && _crashRetryCount < MaxCrashRetries && !Services.Usage.IsRateLimited)
            {
                _crashRetryCount++;
                var retryText = _lastSentText;
                var retryAttachments = _lastSentAttachments;

                Messages.Add(new MessageViewModel(MessageRole.System,
                    $"Session crashed. Auto-restarting (attempt {_crashRetryCount}/{MaxCrashRetries})..."));

                _messageAssembler.ClearAllThinking();
                StopNudgeTimer();
                StatusText = "Restarting...";

                var gen = _sendGeneration;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (_sendGeneration != gen) { IsProcessing = false; return; }
                    _ = SendDirectAsync(retryText, retryAttachments, skipUserMessage: true);
                };
                timer.Start();
                return;
            }

            _messageAssembler.HandleError(error);

            IsProcessing = false;
            StopNudgeTimer();
            _messageAssembler.ClearAllThinking();
            StatusText = "Error";
            UpdateCta(CtaState.WaitingForUser);

            if (MessageQueue.Count > 0)
                Messages.Add(new MessageViewModel(MessageRole.System,
                    $"{MessageQueue.Count} queued message(s) not sent. Click a message to send or return to input."));

            if (_parent.IsTeamPausedForConflict)
                _parent.ResumeTeamAfterConflictPublic();

            Services.Notification.NotifyIfInactive();
        });
    }

    private void HandleFileChanged(string filePath)
    {
        RunOnUI(() =>
        {
            if (!ChangedFiles.Any(f => string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase)))
                ChangedFiles.Add(filePath);
        });
    }

    private bool DetectCompletionMarker()
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Role != MessageRole.Assistant) continue;
            if (Messages[i].IsReviewerMessage) continue;
            if (Messages[i].HasCompletionSummary) return true;

            var text = Messages[i].Text;
            if (string.IsNullOrEmpty(text)) continue;

            var tail = text.Length > 500 ? text[^500..] : text;
            var lower = tail.ToLowerInvariant();

            foreach (var marker in MessageViewModel.CompletionMarkers)
            {
                if (lower.Contains(marker)) return true;
            }
            break;
        }
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  REVIEW
    // ═══════════════════════════════════════════════════════════════════

    private void OnTurnCompleted()
    {
        if (_isAutoReviewPending)
        {
            _isAutoReviewPending = false;
            if (DetectReviewDismissInLastMessage())
            {
                _reviewCycleCompleted = true;
                Messages.Add(new MessageViewModel(MessageRole.System,
                    "Developer dismissed reviewer — low quality feedback. Skipping further reviews."));
                ReviewStatusText = "";
                TryShowTaskSuggestion();
                return;
            }
            TryStartAutoReview();
            return;
        }

        if (_reviewCycleCompleted)
        {
            TryShowTaskSuggestion();
            return;
        }

        var reviewEnabled = Services.Settings.ReviewerEnabled;
        var hasReviewableFiles = HasReviewableFileChanges();

        if (reviewEnabled && hasReviewableFiles)
        {
            _reviewAttempt = 0;
            _lastReviewCriticalSnippet = null;
            TryStartAutoReview();
            return;
        }

        TryShowTaskSuggestion();
    }

    private void TryStartAutoReview()
    {
        var savedAttempt = _reviewAttempt;
        var savedSnippet = _lastReviewCriticalSnippet;
        CancelReview();
        _reviewAttempt = savedAttempt;
        _lastReviewCriticalSnippet = savedSnippet;

        var recentMessages = Messages
            .Skip(Math.Min(_currentTaskStartIndex, Messages.Count))
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant
                        && !(m.Role == MessageRole.Assistant && m.IsReviewerMessage)
                        && !string.IsNullOrEmpty(m.Text))
            .Select(m => (role: m.Role == MessageRole.User ? "user" : "assistant", text: m.Text))
            .ToList();

        var gitDiff = Services.Git.RunGit("diff HEAD", WorkingDirectory);
        var context = ReviewService.BuildReviewContext(ChangedFiles, recentMessages, gitDiff);

        _reviewService = new ReviewService();
        _reviewService.Configure(CliService.ClaudeExePath, WorkingDirectory);

        var maxRounds = Services.Settings.ReviewAutoRetries;
        var roundNum = _reviewAttempt + 1;
        Messages.Add(new MessageViewModel(MessageRole.System,
            _reviewAttempt == 0
                ? "Starting code review..."
                : $"Re-reviewing code (Round {roundNum}/{maxRounds})..."));
        ReviewStatusText = $"Review Round {roundNum}/{maxRounds}";

        _currentReviewerMessage = new MessageViewModel(MessageRole.Assistant)
        {
            IsStreaming = true,
            ReviewerLabel = "Reviewer"
        };
        Messages.Add(_currentReviewerMessage);

        _reviewService.OnTextDelta += text =>
        {
            RunOnUI(() =>
            {
                if (_currentReviewerMessage is not null)
                {
                    _currentReviewerMessage.IsThinking = false;
                    _currentReviewerMessage.Text += text;
                }
                ResetReviewNudgeTimer();
            });
        };

        _reviewService.OnReviewCompleted += (fullText, verdict) =>
        {
            RunOnUI(() =>
            {
                if (_reviewService is null) return;
                if (_currentReviewerMessage is not null)
                {
                    _currentReviewerMessage.IsStreaming = false;
                    _currentReviewerMessage = null;
                }
                HandleReviewVerdict(verdict, fullText);
            });
        };

        _reviewService.OnError += error =>
        {
            RunOnUI(() =>
            {
                if (_reviewService is null) return;
                if (_currentReviewerMessage is not null)
                {
                    _currentReviewerMessage.IsStreaming = false;
                    _currentReviewerMessage.Text = $"Review error: {error}";
                    _currentReviewerMessage = null;
                }
                _reviewService = null;
                IsReviewInProgress = false;
                UpdateCta(CtaState.WaitingForUser);
                StopReviewTimers();
                ReviewStatusText = "";
                Messages.Add(new MessageViewModel(MessageRole.System, "Review failed. Proceeding without review."));
                TryShowTaskSuggestion();
                if (_parent.IsTeamPausedForConflict)
                    _parent.ResumeTeamAfterConflictPublic();
            });
        };

        _reviewService.RunReview(context);
        UpdateCta(CtaState.Reviewing);
        IsReviewInProgress = true;
        StartReviewTimers();
    }

    private void HandleReviewVerdict(ReviewVerdict verdict, string reviewText)
    {
        StopReviewTimers();
        _reviewService?.Stop();
        _reviewService = null;
        IsReviewInProgress = false;

        if (verdict == ReviewVerdict.Consensus)
        {
            _reviewCycleCompleted = true;
            UpdateCta(CtaState.WaitingForUser);
            Messages.Add(new MessageViewModel(MessageRole.System, "Review passed — no issues found."));
            ReviewStatusText = "Review Passed";
            _reviewStatusClearTimer?.Stop();
            _reviewStatusClearTimer = null;
            _reviewStatusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _reviewStatusClearTimer.Tick += (_, _) => { ReviewStatusText = ""; _reviewStatusClearTimer?.Stop(); _reviewStatusClearTimer = null; };
            _reviewStatusClearTimer.Start();
            TryShowTaskSuggestion();
            if (_parent.IsTeamPausedForConflict) _parent.ResumeTeamAfterConflictPublic();
            return;
        }

        _reviewAttempt++;

        if (_reviewAttempt >= Services.Settings.ReviewAutoRetries)
        {
            _reviewCycleCompleted = true;
            Messages.Add(new MessageViewModel(MessageRole.System,
                $"Review found issues after {_reviewAttempt} rounds. Sending final feedback to Claude."));
            ReviewStatusText = $"Review: Final fix (round {_reviewAttempt})";
            Messages.Add(new MessageViewModel(MessageRole.User, "Please fix all remaining issues (final round).")
                { ReviewerLabel = "Final Review Fix" });
            var finalFixPrompt = $"""
                A code reviewer found issues after {_reviewAttempt} review rounds. This is the FINAL round — no more automatic reviews will follow.
                Please carefully fix ALL remaining issues:

                {reviewText}

                After fixing, provide a brief summary of what you changed.
                """;
            SendDirectAsync(finalFixPrompt, null, "Final Review Fix", skipUserMessage: true).ContinueWith(t =>
            {
                if (t.Exception is not null)
                    DiagnosticLogger.Log("REVIEW_FIX_ERROR", t.Exception.InnerException?.Message ?? t.Exception.Message);
            }, TaskContinuationOptions.OnlyOnFaulted);
            return;
        }

        var criticalSnippet = ExtractFirstCritical(reviewText);
        if (criticalSnippet is not null && criticalSnippet == _lastReviewCriticalSnippet)
        {
            _reviewCycleCompleted = true;
            UpdateCta(CtaState.WaitingForUser);
            Messages.Add(new MessageViewModel(MessageRole.System,
                "Review loop detected — same critical issue repeated. Stopping auto-review."));
            ReviewStatusText = "Review: Loop detected";
            TryShowTaskSuggestion();
            if (_parent.IsTeamPausedForConflict) _parent.ResumeTeamAfterConflictPublic();
            return;
        }
        _lastReviewCriticalSnippet = criticalSnippet;

        _isAutoReviewPending = true;
        Messages.Add(new MessageViewModel(MessageRole.User, "Please fix the issues found by the reviewer.")
            { ReviewerLabel = "Auto-Review" });
        var fixPrompt = $"""
            A code reviewer found issues in your recent work:

            {reviewText}

            Please fix the issues identified above. After fixing, provide a brief summary of what you changed.

            Then evaluate the reviewer's feedback quality:
            - If the issues were genuine bugs, security problems, or logic errors → end with `REVIEW_QUALITY: HIGH`
            - If the issues were mostly style preferences, minor suggestions without real impact, or false positives → end with `REVIEW_QUALITY: LOW`
            """;
        SendDirectAsync(fixPrompt, null, "Auto-Review", skipUserMessage: true).ContinueWith(t =>
        {
            if (t.Exception is not null)
                DiagnosticLogger.Log("REVIEW_FIX_ERROR", t.Exception.InnerException?.Message ?? t.Exception.Message);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    internal void CancelReview()
    {
        StopReviewTimers();
        if (_reviewService is not null) { _reviewService.Stop(); _reviewService = null; }
        IsReviewInProgress = false;
        _isAutoReviewPending = false;
        _reviewAttempt = 0;
        _reviewCycleCompleted = false;
        _lastReviewCriticalSnippet = null;
        _reviewStatusClearTimer?.Stop();
        _reviewStatusClearTimer = null;
        ReviewStatusText = "";
        if (_currentReviewerMessage is not null) { _currentReviewerMessage.IsStreaming = false; _currentReviewerMessage = null; }
    }

    private void StartReviewTimers()
    {
        StopReviewTimers();
        var timeoutSeconds = Math.Max(Services.Settings.ReviewTimeoutSeconds, 30);
        var nudgeSeconds = (int)(timeoutSeconds * 0.6);

        _reviewNudgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(nudgeSeconds) };
        _reviewNudgeTimer.Tick += (_, _) =>
        {
            _reviewNudgeTimer?.Stop();
            _reviewNudgeTimer = null;
            if (_reviewService is { IsActive: true })
            {
                Messages.Add(new MessageViewModel(MessageRole.System, "Review taking long, sending nudge..."));
                _reviewService.SendNudge();
            }
        };
        _reviewNudgeTimer.Start();

        _reviewTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(timeoutSeconds) };
        _reviewTimeoutTimer.Tick += (_, _) =>
        {
            _reviewTimeoutTimer?.Stop();
            if (_reviewService is not null)
            {
                var timeStr = timeoutSeconds % 60 == 0 ? $"{timeoutSeconds / 60} min" : $"{timeoutSeconds / 60}m {timeoutSeconds % 60}s";
                Messages.Add(new MessageViewModel(MessageRole.System, $"Review timed out after {timeStr}."));
                CancelReview();
                UpdateCta(CtaState.WaitingForUser);
                TryShowTaskSuggestion();
                if (_parent.IsTeamPausedForConflict) _parent.ResumeTeamAfterConflictPublic();
            }
        };
        _reviewTimeoutTimer.Start();
    }

    private void StopReviewTimers()
    {
        _reviewNudgeTimer?.Stop(); _reviewNudgeTimer = null;
        _reviewTimeoutTimer?.Stop(); _reviewTimeoutTimer = null;
    }

    private void ResetReviewNudgeTimer()
    {
        if (_reviewNudgeTimer is not null) { _reviewNudgeTimer.Stop(); _reviewNudgeTimer.Start(); }
    }

    private bool HasReviewableFileChanges()
    {
        if (string.IsNullOrEmpty(WorkingDirectory) || ChangedFiles.Count == 0) return false;
        var wd = WorkingDirectory.Replace('\\', '/').TrimEnd('/') + "/";
        foreach (var file in ChangedFiles)
        {
            var normalized = file.Replace('\\', '/');
            if (!normalized.StartsWith(wd, StringComparison.OrdinalIgnoreCase)) continue;
            var ext = Path.GetExtension(normalized);
            var fileName = Path.GetFileNameWithoutExtension(normalized);
            if (TextDocExtensions.Contains(ext)) continue;
            if (MediaBinaryExtensions.Contains(ext)) continue;
            if (NonReviewableFileNames.Contains(fileName)) continue;
            if (NonReviewableFullNames.Contains(Path.GetFileName(normalized))) continue;
            return true;
        }
        return false;
    }

    private bool DetectReviewDismissInLastMessage()
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Role != MessageRole.Assistant) continue;
            if (Messages[i].IsReviewerMessage) continue;
            var fullText = Messages[i].Text + (Messages[i].CompletionSummary ?? "");
            return !string.IsNullOrEmpty(fullText) && ReviewService.DetectReviewDismiss(fullText);
        }
        return false;
    }

    private static string? ExtractFirstCritical(string reviewText)
    {
        var lines = reviewText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("CRITICAL", StringComparison.OrdinalIgnoreCase))
            {
                var snippet = string.Join(" ", lines.Skip(i).Take(3)).Trim().ToLowerInvariant();
                return snippet.Length > 200 ? snippet[..200] : snippet;
            }
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  NUDGE
    // ═══════════════════════════════════════════════════════════════════

    private void InitializeNudge()
    {
        _nudgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _nudgeTimer.Tick += NudgeTimer_Tick;
    }

    private void ResetNudgeActivity()
    {
        _lastActivityTime = DateTime.UtcNow;
        if (_showNudgeButton) ShowNudgeButton = false;
        _messageAssembler.CurrentMessage?.ResetThinkingTimer();
    }

    private void StartNudgeTimer()
    {
        _nudgeCount = 0;
        _lastActivityTime = DateTime.UtcNow;
        ShowNudgeButton = false;
        _nudgeTimer?.Start();
    }

    internal void StopNudgeTimer()
    {
        _nudgeTimer?.Stop();
        ShowNudgeButton = false;
    }

    private void NudgeTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsProcessing) { StopNudgeTimer(); return; }
        if (_nudgeCount >= MaxNudgesPerTurn) return;
        var elapsed = (DateTime.UtcNow - _lastActivityTime).TotalSeconds;
        if (elapsed >= NudgeInactivitySeconds && !_showNudgeButton)
            ShowNudgeButton = true;
    }

    internal void ExecuteNudge()
    {
        if (!IsProcessing || _nudgeCount >= MaxNudgesPerTurn) return;
        _nudgeCount++;
        ShowNudgeButton = false;
        _lastActivityTime = DateTime.UtcNow;
        Messages.Add(new MessageViewModel(MessageRole.User, NudgeMessageText));
        CliService.SendMessage(NudgeMessageText);
        DiagnosticLogger.Log("NUDGE", $"Nudge #{_nudgeCount} sent immediately");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONTROL REQUESTS
    // ═══════════════════════════════════════════════════════════════════

    private void HandleControlRequest(string requestId, string toolName, string toolUseId, JsonElement input)
    {
        RunOnUI(() =>
        {
            if (toolName == "ExitPlanMode")
            {
                HandleExitPlanModeControl(requestId, toolUseId, input);
            }
            else if (toolName == "AskUserQuestion")
            {
                HandleAskUserQuestionControl(requestId, toolUseId, input);
            }
            else
            {
                if (toolName is "Write" or "Edit" or "NotebookEdit")
                {
                    var filePath = ExtractFilePathFromToolInput(input);
                    if (filePath != null && _parent.CheckAndHandleFileConflict(requestId, toolUseId, filePath, CliService))
                        return;
                }
                CliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            }
        });
    }

    private void HandleExitPlanModeControl(string requestId, string toolUseId, JsonElement input)
    {
        _exitPlanModeAutoCount++;
        var permissions = ExtractAllowedPrompts(input);
        var ctx = ContextUsageText;

        var autoConfirm = Services.Settings.AutoConfirmPlanMode;

        if (autoConfirm && _exitPlanModeAutoCount <= 2)
        {
            CliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            var msg = string.IsNullOrEmpty(permissions)
                ? $"Plan approved automatically. [{ctx}]"
                : $"Plan approved automatically.\nPermissions: {permissions}\n[{ctx}]";
            Messages.Add(new MessageViewModel(MessageRole.System, msg));
        }
        else
        {
            if (autoConfirm && _exitPlanModeAutoCount > 2)
            {
                // Use parent's property setter so UI toggle updates via PropertyChanged
                _parent.AutoConfirmEnabled = false;
                Messages.Add(new MessageViewModel(MessageRole.System,
                    "Auto-confirm disabled (loop detected). Please confirm manually."));
            }

            _pendingControlRequestId = requestId;
            _pendingControlToolUseId = toolUseId;

            var questionText = "Claude wants to exit plan mode and start implementing.";
            if (!string.IsNullOrEmpty(permissions)) questionText += $"\nPermissions: {permissions}";
            questionText += $"\n[{ctx}]";

            var questionMsg = new MessageViewModel(MessageRole.System, "Exit plan mode?")
            {
                QuestionDisplay = new QuestionDisplayModel
                {
                    QuestionText = questionText,
                    Options =
                    [
                        new QuestionOption { Label = "Yes, go ahead", Description = "Approve plan and start implementation" },
                        new QuestionOption { Label = "No, keep planning", Description = "Stay in plan mode" },
                        new QuestionOption { Label = "New session + plan", Description = "Reset context and continue with plan only" }
                    ]
                }
            };
            Messages.Add(questionMsg);
            UpdateCta(CtaState.AnswerQuestion);
        }
    }

    private void HandleAskUserQuestionControl(string requestId, string toolUseId, JsonElement input)
    {
        _pendingQuestionInput = input.ValueKind != JsonValueKind.Undefined ? input : null;
        _pendingQuestionAnswers.Clear();
        _pendingQuestionMessages.Clear();

        List<(string question, List<QuestionOption> options, bool multiSelect)> parsed;
        int questionCount;
        try
        {
            if (!input.TryGetProperty("questions", out var questionsArr)
                || questionsArr.ValueKind != JsonValueKind.Array
                || questionsArr.GetArrayLength() == 0)
            {
                CliService.SendControlResponse(requestId, "deny", toolUseId: toolUseId,
                    errorMessage: "AskUserQuestion had no questions to display");
                return;
            }

            questionCount = questionsArr.GetArrayLength();
            parsed = new(questionCount);

            foreach (var q in questionsArr.EnumerateArray())
            {
                var question = q.TryGetProperty("question", out var qText) ? qText.GetString() ?? "" : "";
                var multiSelect = q.TryGetProperty("multiSelect", out var ms) && ms.GetBoolean();
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
                parsed.Add((question, options, multiSelect));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            DiagnosticLogger.Log("ASK_QUESTION_ERROR", ex.Message);
            _pendingQuestionInput = null;
            CliService.SendControlResponse(requestId, "deny", toolUseId: toolUseId,
                errorMessage: $"Failed to parse AskUserQuestion input: {ex.Message}");
            return;
        }

        _pendingControlRequestId = requestId;
        _pendingControlToolUseId = toolUseId;
        _pendingQuestionCount = questionCount;

        var questionIdx = 0;
        foreach (var (question, options, multiSelect) in parsed)
        {
            if (options.Count > 0)
            {
                var questionMsg = new MessageViewModel(MessageRole.System, question)
                {
                    QuestionDisplay = new QuestionDisplayModel
                    {
                        QuestionText = question,
                        Options = options,
                        MultiSelect = multiSelect,
                        QuestionIndex = questionIdx
                    }
                };
                Messages.Add(questionMsg);
                _pendingQuestionMessages.Add(questionMsg);
            }
            else
            {
                var placeholderMsg = new MessageViewModel(MessageRole.System, $"Claude asked: {question}");
                Messages.Add(placeholderMsg);
                _pendingQuestionMessages.Add(placeholderMsg);
            }
            questionIdx++;
        }
        UpdateCta(CtaState.AnswerQuestion);
    }

    internal void HandleControlAnswer(string answer)
    {
        if (_pendingControlRequestId is null) return;
        var requestId = _pendingControlRequestId;
        var toolUseId = _pendingControlToolUseId;

        if (_pendingQuestionInput is null)
        {
            ClearQuestionDisplays();
            _pendingControlRequestId = null;
            _pendingControlToolUseId = null;

            if (answer == "Yes, go ahead")
                CliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            else if (answer == "New session + plan")
            {
                CliService.SendControlResponse(requestId, "deny",
                    errorMessage: "User chose to reset context and continue with plan only");
                Messages.Add(new MessageViewModel(MessageRole.User, answer));
                StartNewSession();
                return;
            }
            else
                CliService.SendControlResponse(requestId, "deny", errorMessage: "User chose to keep planning");

            Messages.Add(new MessageViewModel(MessageRole.User, answer));
            UpdateCta(CtaState.Processing);
            return;
        }

        try
        {
            var input = _pendingQuestionInput.Value;
            if (input.TryGetProperty("questions", out var questionsArr) && questionsArr.ValueKind == JsonValueKind.Array)
            {
                var questions = questionsArr.EnumerateArray().ToList();
                var idx = _pendingQuestionAnswers.Count;
                var isMultiSelectConfirm = idx < _pendingQuestionMessages.Count
                    && _pendingQuestionMessages[idx]?.QuestionDisplay is { MultiSelect: true };
                var isButtonClick = isMultiSelectConfirm
                    || (idx < questions.Count
                    && questions[idx].TryGetProperty("options", out var opts)
                    && opts.ValueKind == JsonValueKind.Array
                    && opts.EnumerateArray().Any(o =>
                        o.TryGetProperty("label", out var l) && l.GetString() == answer));

                if (isButtonClick)
                {
                    var questionText = questions[idx].TryGetProperty("question", out var qt) ? qt.GetString() ?? "" : "";
                    _pendingQuestionAnswers.Add((questionText, answer));
                    if (idx < _pendingQuestionMessages.Count && _pendingQuestionMessages[idx]?.QuestionDisplay is { } answered)
                        answered.IsAnswered = true;
                }
                else
                {
                    for (var i = _pendingQuestionAnswers.Count; i < questions.Count; i++)
                    {
                        var questionText = questions[i].TryGetProperty("question", out var qt) ? qt.GetString() ?? "" : "";
                        _pendingQuestionAnswers.Add((questionText, answer));
                    }
                    ClearQuestionDisplays();
                }
            }
        }
        catch (JsonException ex)
        {
            DiagnosticLogger.Log("CONTROL_ANSWER_JSON_ERROR", ex.Message);
            var remaining = _pendingQuestionCount - _pendingQuestionAnswers.Count;
            for (var i = 0; i < remaining; i++)
                _pendingQuestionAnswers.Add(("unknown", answer));
        }

        if (_pendingQuestionAnswers.Count >= _pendingQuestionCount)
        {
            ClearQuestionDisplays();

            var answersDict = new Dictionary<string, string>();
            for (var i = 0; i < _pendingQuestionAnswers.Count; i++)
            {
                var key = _pendingQuestionAnswers[i].question;
                if (answersDict.ContainsKey(key)) key = $"{key} ({i + 1})";
                answersDict[key] = _pendingQuestionAnswers[i].answer;
            }

            var questionsJson = "[]";
            try
            {
                if (_pendingQuestionInput?.TryGetProperty("questions", out var qa) == true)
                    questionsJson = qa.GetRawText();
            }
            catch { }

            var answersJson = JsonSerializer.Serialize(answersDict);
            var updatedInputJson = "{\"questions\":" + questionsJson + ",\"answers\":" + answersJson + "}";

            CliService.SendControlResponse(requestId, "allow", updatedInputJson: updatedInputJson, toolUseId: toolUseId);

            var shownAnswers = new HashSet<string>();
            foreach (var (q, a) in _pendingQuestionAnswers)
            {
                if (shownAnswers.Add(a))
                    Messages.Add(new MessageViewModel(MessageRole.User, a));
            }

            _pendingControlRequestId = null;
            _pendingControlToolUseId = null;
            _pendingQuestionInput = null;
            _pendingQuestionAnswers.Clear();
            _pendingQuestionMessages.Clear();
            _pendingQuestionCount = 0;
            UpdateCta(CtaState.Processing);
        }
    }

    private void ClearQuestionDisplays()
    {
        foreach (var msg in Messages)
        {
            if (msg.QuestionDisplay is { IsAnswered: false })
                msg.QuestionDisplay.IsAnswered = true;
        }
    }

    internal bool HasPendingControlRequest => _pendingControlRequestId is not null;

    private static string ExtractAllowedPrompts(JsonElement input)
    {
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return "";
        try
        {
            if (!input.TryGetProperty("allowedPrompts", out var prompts) || prompts.ValueKind != JsonValueKind.Array) return "";
            var sb = new StringBuilder();
            foreach (var p in prompts.EnumerateArray())
            {
                var tool = p.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : "";
                var prompt = p.TryGetProperty("prompt", out var pr) ? pr.GetString() ?? "" : "";
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"{tool}({prompt})");
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    private static string? ExtractFilePathFromToolInput(JsonElement input)
    {
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (input.TryGetProperty("file_path", out var fp) && fp.ValueKind == JsonValueKind.String) return fp.GetString();
        if (input.TryGetProperty("notebook_path", out var np) && np.ValueKind == JsonValueKind.String) return np.GetString();
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BACKGROUND TASKS
    // ═══════════════════════════════════════════════════════════════════

    private void InitializeBackgroundTaskTimer()
    {
        _bgTaskTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _bgTaskTimer.Tick += BgTaskTimer_Tick;
    }

    private void BgTaskTimer_Tick(object? sender, EventArgs e)
    {
        var hasRunning = false;
        foreach (var task in BackgroundTasks)
        {
            if (task.Status == BackgroundTaskStatus.Running) { task.UpdateElapsed(); hasRunning = true; }
        }
        UpdateBackgroundTasksHeader();
        if (!hasRunning) _bgTaskTimer?.Stop();
    }

    private void TryTrackBackgroundTask(string toolName, string toolUseId, string input)
    {
        if (toolName != "Task" || string.IsNullOrEmpty(input) || !input.StartsWith('{')) return;
        if (BackgroundTasks.Any(t => t.ToolUseId == toolUseId)) return;

        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            if (!root.TryGetProperty("run_in_background", out var bgProp) || !bgProp.GetBoolean()) return;
            var description = root.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "Background task" : "Background task";
            var info = new BackgroundTaskInfo(toolUseId, description, DateTime.UtcNow);
            BackgroundTasks.Add(new BackgroundTaskViewModel(info));
            HasBackgroundTasks = true;
            UpdateBackgroundTasksHeader();
            if (_bgTaskTimer is { IsEnabled: false }) _bgTaskTimer.Start();
        }
        catch { }
    }

    private void TryUpdateBackgroundTask(string toolName, string toolUseId, string content)
    {
        if (toolName == "Task")
        {
            var bgTask = BackgroundTasks.FirstOrDefault(t => t.ToolUseId == toolUseId);
            if (bgTask is null) return;
            var agentIdMatch = Regex.Match(content, @"agentId:\s*(\S+)");
            if (agentIdMatch.Success) bgTask.AgentId = agentIdMatch.Groups[1].Value;
        }
        else if (toolName == "TaskOutput")
        {
            var taskId = FindTaskIdForToolResult(toolUseId);
            if (taskId is null) return;
            var bgTask = BackgroundTasks.FirstOrDefault(t => t.AgentId == taskId);
            if (bgTask is null) return;
            if (!string.IsNullOrWhiteSpace(content) && content.Length > 20) { bgTask.Complete(content); UpdateBackgroundTasksHeader(); }
        }
        else if (toolName == "TaskStop")
        {
            var taskId = FindTaskIdForToolResult(toolUseId);
            if (taskId is null) return;
            var bgTask = BackgroundTasks.FirstOrDefault(t => t.AgentId == taskId);
            if (bgTask is null) return;
            bgTask.Fail();
            UpdateBackgroundTasksHeader();
        }
    }

    private string? FindTaskIdForToolResult(string toolUseId)
    {
        if (_messageAssembler.CurrentMessage is null) return null;
        var toolVm = _messageAssembler.CurrentMessage.ToolUses.FirstOrDefault(t => t.ToolUseId == toolUseId);
        if (toolVm?.Input is null || !toolVm.Input.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(toolVm.Input);
            return doc.RootElement.TryGetProperty("task_id", out var tid) ? tid.GetString() : null;
        }
        catch { return null; }
    }

    internal void ExecuteSendBgTaskOutput(object? param)
    {
        if (param is not BackgroundTaskViewModel bgTask || !bgTask.HasOutput || bgTask.IsSent) return;
        bgTask.IsSent = true;
        var prompt = $"Output from background task \"{bgTask.Description}\":\n\n<background-task-output>\n{bgTask.OutputFull}\n</background-task-output>";
        _ = SendDirectAsync(prompt, null);
    }

    internal void ExecuteDismissBgTask(object? param)
    {
        if (param is not BackgroundTaskViewModel bgTask) return;
        BackgroundTasks.Remove(bgTask);
        HasBackgroundTasks = BackgroundTasks.Count > 0;
        UpdateBackgroundTasksHeader();
    }

    private void UpdateBackgroundTasksHeader()
    {
        var total = BackgroundTasks.Count;
        var running = BackgroundTasks.Count(t => t.Status == BackgroundTaskStatus.Running);
        BackgroundTasksHeaderText = running > 0
            ? $"Background Tasks ({total} \u2014 {running} running)"
            : $"Background Tasks ({total})";
    }

    private void ClearBackgroundTasks()
    {
        BackgroundTasks.Clear();
        HasBackgroundTasks = false;
        _bgTaskTimer?.Stop();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UTILITIES
    // ═══════════════════════════════════════════════════════════════════

    internal void CancelProcessing()
    {
        _sendGeneration++;
        CliService.Cancel();
        CancelReview();
        IsProcessing = false;
        _crashRetryCount = 0;
        StopNudgeTimer();
        StatusText = "Cancelled";
        UpdateCta(CtaState.WaitingForUser);

        if (_messageAssembler.CurrentMessage is not null)
        {
            _messageAssembler.CurrentMessage.IsStreaming = false;
            _messageAssembler.CurrentMessage.IsThinking = false;
            _messageAssembler.Reset();
        }
        _messageAssembler.ClearAllThinking();

        if (_parent.IsTeamPausedForConflict)
            _parent.ResumeTeamAfterConflictPublic();
    }

    public bool RecallLastMessage()
    {
        if (!IsProcessing || _hasResponseStarted || _lastSentText is null) return false;

        CliService.Cancel();
        IsProcessing = false;
        StatusText = "";
        UpdateCta(CtaState.WaitingForUser);

        if (_messageAssembler.CurrentMessage is not null)
        {
            var thinkingMsg = _messageAssembler.CurrentMessage;
            Messages.Remove(thinkingMsg);
            thinkingMsg.Dispose();
            _messageAssembler.Reset();
        }

        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Role == MessageRole.User)
            {
                Messages[i].Dispose();
                Messages.RemoveAt(i);
                break;
            }
        }

        InputText = StripInlineMarkers(_lastSentText);
        if (_lastSentAttachments is not null)
            foreach (var att in _lastSentAttachments) AddAttachment(att);

        _lastSentText = null;
        _lastSentAttachments = null;
        return true;
    }

    public bool HandleEscape()
    {
        if (MessageQueue.Count > 0)
        {
            var last = MessageQueue[^1];
            MessageQueue.RemoveAt(MessageQueue.Count - 1);
            InputText = StripInlineMarkers(last.Text);
            if (last.Attachments != null)
                foreach (var att in last.Attachments) AddAttachment(att);
            return true;
        }

        if (IsProcessing)
        {
            if (RecallLastMessage()) return true;
            var textToRestore = _lastSentText;
            var attachmentsToRestore = _lastSentAttachments;
            _lastSentText = null;
            _lastSentAttachments = null;
            CancelProcessing();
            if (textToRestore != null) InputText = StripInlineMarkers(textToRestore);
            if (attachmentsToRestore != null)
                foreach (var att in attachmentsToRestore) AddAttachment(att);
            return true;
        }

        if (_reviewService is not null)
        {
            CancelReview();
            StatusText = "Review cancelled";
            UpdateCta(CtaState.WaitingForUser);
            Messages.Add(new MessageViewModel(MessageRole.System, "Review cancelled by user."));
            if (_parent.IsTeamPausedForConflict) _parent.ResumeTeamAfterConflictPublic();
            return true;
        }

        return false;
    }

    internal void UpdateCta(CtaState state)
    {
        _ctaState = state;
        CtaText = state switch
        {
            CtaState.Ready => "Start a conversation with Claude",
            CtaState.Processing => "Claude is working. Press \u2191 to recall, Escape to cancel, or send to queue.",
            CtaState.WaitingForUser => "Claude is waiting for your response",
            CtaState.AnswerQuestion => "Answer the question above",
            CtaState.ConfirmOperation => "Confirm the operation above",
            CtaState.Reviewing => "Review in progress. Wait for completion or press Escape to cancel.",
            _ => ""
        };
        OnPropertyChanged(nameof(HasCta));
    }

    public void AddAttachment(FileAttachment attachment)
    {
        if (Attachments.All(a => a.FilePath != attachment.FilePath))
            Attachments.Add(attachment);
    }

    public void AddTaskOutput(string taskName, string output)
    {
        RunOnUI(() =>
        {
            var displayOutput = output.Length > 5000 ? output[..5000] + "\n... (truncated)" : output;
            var msg = new MessageViewModel(MessageRole.System, $"Task \"{taskName}\" completed")
            {
                TaskOutputFull = output,
                TaskOutputText = displayOutput
            };
            Messages.Add(msg);
        });
    }

    private void ResetTaskOutputSentFlags()
    {
        foreach (var msg in Messages)
        {
            if (msg.HasTaskOutput && msg.IsTaskOutputSent)
                msg.IsTaskOutputSent = false;
        }
    }

    internal void ShowFileDiff(string filePath)
    {
        var oldContent = CliService.GetFileSnapshot(filePath);
        string? newContent;
        try { newContent = File.Exists(filePath) ? File.ReadAllText(filePath) : null; }
        catch { newContent = null; }

        if (oldContent is null && newContent is null)
        {
            MessageBox.Show($"Cannot read file:\n{filePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var diff = DiffService.ComputeDiff(oldContent, newContent);
        var viewer = new DiffViewerWindow(filePath, diff);
        if (Application.Current?.MainWindow is { } mainWin) viewer.Owner = mainWin;
        viewer.Show();
    }

    private void UpdateEffectiveProject(string toolName, string inputJson)
    {
        string? filePath = null;
        try
        {
            if (string.IsNullOrEmpty(inputJson) || !inputJson.StartsWith('{')) return;
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            filePath = toolName switch
            {
                "Read" or "Write" or "Edit" => root.TryGetProperty("file_path", out var fp) ? fp.GetString() : null,
                "NotebookEdit" => root.TryGetProperty("notebook_path", out var np) ? np.GetString() : null,
                "Glob" or "Grep" => root.TryGetProperty("path", out var p) ? p.GetString() : null,
                _ => null
            };
        }
        catch { return; }

        if (string.IsNullOrEmpty(filePath) || !Path.IsPathRooted(filePath)) return;
        var projectPath = FindProjectForFile(filePath);
        if (projectPath is null) return;
        var projectName = Path.GetFileName(projectPath.TrimEnd('\\', '/'));
        var workingName = Path.GetFileName(WorkingDirectory?.TrimEnd('\\', '/') ?? "");
        EffectiveProjectName = string.Equals(projectName, workingName, StringComparison.OrdinalIgnoreCase) ? "" : projectName;
    }

    private void UpdateTodoProgress(string inputJson)
    {
        try
        {
            if (string.IsNullOrEmpty(inputJson)) return;
            using var doc = JsonDocument.Parse(inputJson);
            if (!doc.RootElement.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array) return;
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
        var settings = Services.Settings;
        var hasKey = !string.IsNullOrEmpty(settings.SshKeyPath);
        var sshPassword = SettingsService.Unprotect(settings.SshMasterPasswordProtected ?? "");
        var hasPassword = !string.IsNullOrEmpty(sshPassword);
        var hasServers = settings.Servers.Count > 0;
        if (!hasKey && !hasPassword && !hasServers) return null;

        var lines = new List<string> { "## SSH Access" };
        if (hasKey)
        {
            lines.Add($"- Claude's SSH private key path: `{settings.SshKeyPath}`");
            lines.Add($"- When deploying or connecting via SSH, use this key with `-i \"{settings.SshKeyPath}\"` flag");
        }
        if (hasPassword)
        {
            lines.Add($"- SSH master password for servers that don't accept key auth: `{sshPassword}`");
            lines.Add("- Use `sshpass -p '{password}' ssh ...` when key-based auth is not available");
        }
        if (hasServers)
        {
            lines.Add("");
            lines.Add("### Known servers");
            foreach (var s in settings.Servers)
            {
                var desc = !string.IsNullOrEmpty(s.Description) ? $" — {s.Description}" : "";
                var projects = s.Projects.Count > 0 ? $" (Projects: {string.Join(", ", s.Projects)})" : "";
                lines.Add($"- **{s.Name}**: `{s.User}@{s.Host}:{s.Port}`{desc}{projects}");
            }
        }
        return string.Join("\n", lines);
    }

    private void CheckApiKeyExpiry()
    {
        if (_apiKeyExpiryChecked || Services.Settings.ApiKeys.Count == 0) return;
        _apiKeyExpiryChecked = true;

        var warnings = new List<string>();
        foreach (var key in Services.Settings.ApiKeys)
        {
            var (days, isExpired, isWarning) = key.GetExpiryStatus();
            if (isExpired) warnings.Add($"{key.ServiceName} API key expired {-days} days ago");
            else if (isWarning) warnings.Add(days == 0 ? $"{key.ServiceName} API key expires today" : $"{key.ServiceName} API key expires in {days} days");
        }
        if (warnings.Count > 0)
            Messages.Add(new MessageViewModel(MessageRole.System,
                "API key warning: " + string.Join("; ", warnings) + ". Go to Settings > API Keys to update."));
    }

    internal static string StripInlineMarkers(string text)
    {
        if (!text.Contains("[Screenshot:") && !text.Contains("[File:")) return text;
        return Regex.Replace(text, @"\r?\n?\[(?:Screenshot|File): [^\]]+\]\r?\n?", "\n").Trim();
    }

    private void TryShowTaskSuggestion()
    {
        if (_parent.TaskRunner is null || string.IsNullOrEmpty(WorkingDirectory)) return;
        if (ChangedFiles.Count == 0) return;
        if (!_reviewCycleCompleted && !DetectCompletionMarker()) return;

        var effectiveProjects = GetEffectiveProjectPaths();
        if (effectiveProjects.Count > 0 && effectiveProjects.All(p =>
                Services.Settings.TaskSuggestionDismissedProjects.Contains(
                    p.NormalizePath(), StringComparer.OrdinalIgnoreCase)))
            return;

        var suggestions = new List<TaskSuggestionItem>();
        var addedTaskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasGit = false;

        foreach (var projectPath in effectiveProjects)
        {
            var projectTasks = _parent.TaskRunner!.GetTasksForProject(projectPath);
            var deployTasks = projectTasks
                .Where(t => t.Command.Contains("deploy", StringComparison.OrdinalIgnoreCase)
                            || t.Command.Contains("publish", StringComparison.OrdinalIgnoreCase))
                .Take(3);

            foreach (var dt in deployTasks)
            {
                if (addedTaskNames.Add(dt.Name))
                    suggestions.Add(new TaskSuggestionItem { Label = dt.Name, Task = dt });
            }

            if (!hasGit && IsInsideGitRepo(projectPath)) hasGit = true;
        }

        if (hasGit) suggestions.Add(new TaskSuggestionItem { Label = "Commit & Push", IsCommit = true });
        if (suggestions.Count == 0) return;

        FinalizeActions.SuggestedTasks.Clear();
        foreach (var s in suggestions) FinalizeActions.SuggestedTasks.Add(s);

        FinalizeActions.ProjectName = effectiveProjects.Count == 1
            ? Path.GetFileName(effectiveProjects[0])
            : string.Join(", ", effectiveProjects.Select(Path.GetFileName));
        FinalizeActions.HasCompletedTask = true;
        FinalizeActions.ShowFinalizeActionsLabel = false;
        FinalizeActions.ShowTaskSuggestion = true;
        FinalizeActions.StartAutoCollapseTimer();
    }

    private List<string> GetEffectiveProjectPaths()
    {
        var projectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in ChangedFiles)
        {
            var match = FindProjectForFile(filePath);
            if (match is not null) projectPaths.Add(match);
        }
        if (projectPaths.Count == 0 && !string.IsNullOrEmpty(WorkingDirectory))
            projectPaths.Add(WorkingDirectory);
        return projectPaths.ToList();
    }

    private string? FindProjectForFile(string filePath)
    {
        string? bestMatch = null;
        var bestLength = 0;
        foreach (var project in Services.ProjectRegistry.Projects)
        {
            var projectPath = project.Path.NormalizePath();
            if (filePath.IsSubPathOf(projectPath) && projectPath.Length > bestLength)
            { bestMatch = project.Path; bestLength = projectPath.Length; }
        }
        return bestMatch;
    }

    private static bool IsInsideGitRepo(string path)
    {
        var dir = path;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git"))) return true;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir) break;
            dir = parent;
        }
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SESSION MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════

    internal void StartNewSession()
    {
        if (IsProcessing) CancelProcessing();

        _parent.TechnicalWriter?.Flush();
        SaveChatHistory();
        _currentChatId = null;

        _messageAssembler.ClearMessages();
        MessageQueue.Clear();
        ChangedFiles.Clear();
        ClearBackgroundTasks();
        CliService.ClearFileSnapshots();
        _crashRetryCount = 0;
        CliService.ResetSessionSync();
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

        FinalizeActions.ShowTaskSuggestion = false;
        FinalizeActions.ShowFinalizeActionsLabel = false;
        FinalizeActions.HasCompletedTask = false;
        FinalizeActions.SuggestedTasks.Clear();
        FinalizeActions.StopTaskSuggestionTimer();
        StopNudgeTimer();

        if (!string.IsNullOrEmpty(WorkingDirectory)
            && Services.Settings.SavedSessions.Remove(WorkingDirectory))
            Services.SettingsService.Save(Services.Settings);

        if (Services.Settings.ContextSnapshotEnabled && !string.IsNullOrEmpty(WorkingDirectory))
        {
            Services.ContextSnapshot.InvalidateAll();
            Services.ContextSnapshot.StartGenerationInBackground([WorkingDirectory]);
        }

        UpdateCta(CtaState.Ready);
    }

    internal void SwitchToOpus()
    {
        CliService.ModelOverride = "opus";
        StartNewSession();
        Messages.Add(new MessageViewModel(MessageRole.System, "Switching to Opus. Next message will use claude-opus."));
    }

    private async Task DetectChangedFilesFromGitAsync()
    {
        if (string.IsNullOrEmpty(WorkingDirectory)) return;
        var baseline = _preTurnDirtyFiles;
        var files = await Task.Run(() => Services.Git.GetChangedFiles(WorkingDirectory));
        foreach (var file in files)
        {
            if (!baseline.Contains(file) && !ChangedFiles.Any(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
                ChangedFiles.Add(file);
        }
    }

    internal void SaveChatHistory()
    {
        var chatMessages = Messages.Where(m => m.Role is MessageRole.User or MessageRole.Assistant).ToList();
        if (chatMessages.Count == 0) return;

        var entry = new ChatHistoryEntry
        {
            Id = _currentChatId ?? Guid.NewGuid().ToString(),
            ProjectPath = WorkingDirectory,
            SessionId = CliService.SessionId,
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

        var firstUser = chatMessages.FirstOrDefault(m => m.Role == MessageRole.User);
        entry.Title = firstUser is not null
            ? (firstUser.Text.Length > 80 ? firstUser.Text[..80] + "..." : firstUser.Text)
            : "Untitled";

        if (_currentChatId is null)
        {
            entry.CreatedAt = chatMessages[0].Timestamp;
            _currentChatId = entry.Id;
        }

        try { Services.ChatHistory.Save(entry); } catch { }
    }

    internal void LoadChatFromHistory(ChatHistoryEntry entry)
    {
        if (IsProcessing) CancelProcessing();

        _messageAssembler.ClearMessages();
        MessageQueue.Clear();
        _currentChatId = entry.Id;

        if (!string.IsNullOrEmpty(entry.SessionId))
            CliService.RestoreSession(entry.SessionId);
        else
            CliService.ResetSessionAsync().ContinueWith(t =>
                DiagnosticLogger.Log("RESET_SESSION_ERROR", t.Exception?.InnerException?.Message ?? "unknown"),
                TaskContinuationOptions.OnlyOnFaulted);
        _needsPreambleInjection = true;
        ResetTaskOutputSentFlags();

        foreach (var msg in entry.Messages)
        {
            var vm = new MessageViewModel(msg.Role, msg.Text);
            foreach (var tool in msg.ToolUses)
                vm.ToolUses.Add(new ToolUseViewModel(tool.ToolName, tool.ToolUseId, tool.Input));
            Messages.Add(vm);
        }

        if (!string.IsNullOrEmpty(entry.ProjectPath) && entry.ProjectPath != WorkingDirectory)
            _parent.SetWorkingDirectory(entry.ProjectPath);

        ModelName = "";
        StatusText = "";
        Messages.Add(new MessageViewModel(MessageRole.System,
            $"Loaded chat from history. {(entry.SessionId is not null ? "Session restored — you can continue." : "No session to restore.")}"));
        UpdateCta(CtaState.WaitingForUser);
    }

    // ─── Composer helpers ───

    private (string text, List<FileAttachment>? inlineAttachments, List<MessageContentPart>? contentParts) BuildComposerContent()
    {
        var sb = new StringBuilder();
        List<FileAttachment>? inlineAtts = null;
        List<MessageContentPart>? contentParts = null;
        bool hasImages = ComposerBlocks.Any(b => b is ImageComposerBlock);

        foreach (var block in ComposerBlocks)
        {
            switch (block)
            {
                case TextComposerBlock tb:
                    sb.Append(tb.Text);
                    if (hasImages && !string.IsNullOrEmpty(tb.Text))
                        (contentParts ??= []).Add(MessageContentPart.CreateText(tb.Text));
                    break;
                case ImageComposerBlock ib:
                    (inlineAtts ??= []).Add(ib.Attachment);
                    (contentParts ??= []).Add(MessageContentPart.CreateImage(ib.Attachment));
                    sb.AppendLine();
                    sb.AppendLine(ib.Attachment.IsScreenshot ? $"[Screenshot: {ib.FilePath}]" : $"[File: {ib.FilePath}]");
                    break;
            }
        }
        return (sb.ToString().Trim(), inlineAtts, contentParts);
    }

    public void ClearComposerText()
    {
        ComposerBlocks.Clear();
        ComposerBlocks.Add(new TextComposerBlock());
        OnPropertyChanged(nameof(IsComposerEmpty));
    }

    public void ClearComposer()
    {
        ClearComposerText();
        Attachments.Clear();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  COMMAND HELPERS (called from MainViewModel command lambdas)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Send a queued message immediately, cancelling current processing.</summary>
    internal void HandleSendQueuedNow(QueuedMessage qm)
    {
        MessageQueue.Remove(qm);
        CancelProcessing();
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (IsProcessing) return;
            _currentTaskStartIndex = Messages.Count;
            _reviewCycleCompleted = false;
            ChangedFiles.Clear();
            _ = SendDirectAsync(qm.Text, qm.Attachments);
        });
    }

    /// <summary>Return a queued message back to the input box.</summary>
    internal void HandleReturnQueuedToInput(QueuedMessage qm)
    {
        MessageQueue.Remove(qm);
        InputText = StripInlineMarkers(qm.Text);
        if (qm.Attachments is not null)
        {
            foreach (var att in qm.Attachments)
                AddAttachment(att);
        }
    }

    /// <summary>Handle the AnswerQuestionCommand logic (multi-select, confirm, delegate to HandleControlAnswer).</summary>
    internal void HandleAnswerCommand(string answer)
    {
        if (_pendingControlRequestId is null) return;

        // Multi-select toggle
        if (!answer.StartsWith("__confirm_multiselect__"))
        {
            var msMsg = _pendingQuestionMessages.FirstOrDefault(m =>
                m.QuestionDisplay is { MultiSelect: true, IsAnswered: false }
                && m.QuestionDisplay.Options.Any(o => o.Label == answer));
            if (msMsg?.QuestionDisplay is { } display)
            {
                var option = display.Options.FirstOrDefault(o => o.Label == answer);
                if (option is not null)
                    option.IsSelected = !option.IsSelected;
                return;
            }
        }

        // Multi-select confirm
        if (answer.StartsWith("__confirm_multiselect__"))
        {
            var colonPos = answer.IndexOf(':');
            if (colonPos < 0) return;
            if (!int.TryParse(answer.AsSpan(colonPos + 1), out var confirmIdx) || confirmIdx < 0)
                return;

            if (confirmIdx < _pendingQuestionMessages.Count
                && _pendingQuestionMessages[confirmIdx]?.QuestionDisplay is { MultiSelect: true } msDisplay)
            {
                var selected = msDisplay.Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
                if (selected.Count == 0) return;
                answer = string.Join(", ", selected);
            }
            else
                return;
        }

        HandleControlAnswer(answer);
    }

    /// <summary>Send task output to Claude.</summary>
    internal void HandleSendTaskOutput(MessageViewModel msg)
    {
        if (!msg.HasTaskOutput || msg.IsTaskOutputSent) return;
        msg.IsTaskOutputSent = true;
        var fullOutput = msg.TaskOutputFull ?? msg.TaskOutputText;
        var prompt = $"Console output from task \"{msg.Text}\":\n\n<task-output>\n{fullOutput}\n</task-output>";
        _ = SendDirectAsync(prompt, null);
    }
}
