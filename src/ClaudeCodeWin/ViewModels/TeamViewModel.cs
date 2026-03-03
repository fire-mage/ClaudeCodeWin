using System.Collections.ObjectModel;
using System.Media;
using System.Text;
using System.Windows;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public class TeamViewModel : ViewModelBase, IDisposable
{
    private readonly BacklogService _backlogService;
    private readonly GitService _gitService;
    private readonly Func<string?> _getProjectPath;
    private readonly PlannerService _plannerService;
    private readonly PlanReviewerService _planReviewerService;
    private readonly TeamOrchestratorService _orchestratorService;
    private readonly NotificationService _notificationService;
    private readonly ProjectRegistryService _projectRegistry;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private string _projectName = "";
    private string _orchestratorStatusText = "Stopped";
    private bool _isOrchestratorStopped = true;
    private string _orchestratorLog = "";
    private bool _showArchive;

    // Live chat viewer state (plain-text for inline preview)
    private string? _activeDevelopingFeatureId;
    private string _devChatText = "";
    private string _reviewChatText = "";
    private string _devChatLabel = "Development";
    private readonly StringBuilder _devChatBuffer = new();
    private readonly StringBuilder _reviewChatBuffer = new();
    private System.Windows.Threading.DispatcherTimer? _chatFlushTimer;
    private bool _chatBufferDirty;

    // Rich team chat (structured messages for popup window)
    private readonly ChatMessageAssembler _teamChatAssembler;
    public ObservableCollection<MessageViewModel> TeamChatMessages { get; } = [];

    // Pipeline collections
    public ObservableCollection<BacklogFeatureVM> PlanningFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> AwaitingApprovalFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> BacklogFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> QueuedFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> CompletedFeatures { get; } = [];
    public ObservableCollection<SessionHealthInfo> SessionHealthItems { get; } = [];
    public bool HasActiveSession => SessionHealthItems.Count > 0;

    // Metrics panel computed properties (derived from SessionHealthItems[0])
    public string ActiveElapsed => SessionHealthItems.Count > 0 ? SessionHealthItems[0].Elapsed : "";

    public string ActiveRole => SessionHealthItems.Count > 0 ? SessionHealthItems[0].Role switch
    {
        "Dev" => "Developer",
        "Dev (fixing)" => "Developer (fixing)",
        "Review" => "Reviewer",
        _ => SessionHealthItems[0].Role
    } : "";

    public string ActiveReviewRound => SessionHealthItems.Count > 0
        && SessionHealthItems[0].ReviewRound > 0
        && SessionHealthItems[0].MaxReviewRounds > 0
        ? $"Round {SessionHealthItems[0].ReviewRound}/{SessionHealthItems[0].MaxReviewRounds}"
        : "";

    public int ActiveIdleSeconds => SessionHealthItems.Count > 0 ? SessionHealthItems[0].IdleSeconds : 0;

    public string ActiveIdleText => SessionHealthItems.Count > 0 && SessionHealthItems[0].IdleSeconds > 0
        ? $"Idle {SessionHealthItems[0].IdleSeconds}s"
        : "";

    public bool IsIdleWarning => ActiveIdleSeconds >= 30;

    public ObservableCollection<BacklogFeatureVM> ArchivedFeatures { get; } = [];
    public int ArchivedCount => ArchivedFeatures.Count;

    public bool ShowArchive
    {
        get => _showArchive;
        set => SetProperty(ref _showArchive, value);
    }

    public string? ActiveDevelopingFeatureId
    {
        get => _activeDevelopingFeatureId;
        private set
        {
            SetProperty(ref _activeDevelopingFeatureId, value);
            OnPropertyChanged(nameof(ActiveFeatureTitle));
        }
    }

    /// <summary>Title of the currently active feature (for TeamChatWindow header).</summary>
    public string ActiveFeatureTitle
    {
        get
        {
            if (_activeDevelopingFeatureId is null) return "No active task";
            var feature = _backlogService.GetFeatures(_getProjectPath())
                .FirstOrDefault(f => f.Id == _activeDevelopingFeatureId);
            return feature?.Title ?? feature?.RawIdea ?? _activeDevelopingFeatureId;
        }
    }

    public string DevChatText
    {
        get => _devChatText;
        private set => SetProperty(ref _devChatText, value);
    }

    public string ReviewChatText
    {
        get => _reviewChatText;
        private set => SetProperty(ref _reviewChatText, value);
    }

    public string DevChatLabel
    {
        get => _devChatLabel;
        private set => SetProperty(ref _devChatLabel, value);
    }

    /// <summary>
    /// Event raised when user wants to ask about a feature in main chat.
    /// MainViewModel subscribes to this to populate the input box.
    /// </summary>
    public event Action<string>? OnAskInChat;

    // Section counts for badge binding
    public int PlanningCount => PlanningFeatures.Count;
    public int ApprovalCount => AwaitingApprovalFeatures.Count;
    public int BacklogCount => BacklogFeatures.Count;
    public int QueuedCount => QueuedFeatures.Count;
    public int CompletedCount => CompletedFeatures.Count;

    public string OrchestratorStatusText
    {
        get => _orchestratorStatusText;
        set => SetProperty(ref _orchestratorStatusText, value);
    }

    public bool IsOrchestratorStopped
    {
        get => _isOrchestratorStopped;
        private set => SetProperty(ref _isOrchestratorStopped, value);
    }

    public string OrchestratorLog
    {
        get => _orchestratorLog;
        set => SetProperty(ref _orchestratorLog, value);
    }

    public RelayCommand DeleteFeatureCommand { get; }
    public RelayCommand CancelFeatureCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand AnswerQuestionCommand { get; }
    public RelayCommand StartOrchestratorCommand { get; }
    public RelayCommand PauseOrchestratorCommand { get; }
    public RelayCommand HardPauseOrchestratorCommand { get; }
    public RelayCommand ApprovePlanCommand { get; }
    public RelayCommand RejectPlanCommand { get; }
    public RelayCommand RequestAIReviewCommand { get; }
    public RelayCommand RetryPlanningCommand { get; }
    public RelayCommand AddToQueueCommand { get; }
    public RelayCommand ReturnToBacklogCommand { get; }
    public RelayCommand ViewHistoryCommand { get; }
    public RelayCommand CommitFeatureCommand { get; }
    public RelayCommand ArchiveFeatureCommand { get; }
    public RelayCommand CommitAllCommand { get; }
    public RelayCommand ArchiveAllCommand { get; }
    public RelayCommand AskInChatCommand { get; }
    public RelayCommand ToggleArchiveCommand { get; }
    public RelayCommand DiscussPlanCommand { get; }
    public RelayCommand SubmitDiscussionCommand { get; }
    public RelayCommand CancelDiscussionCommand { get; }
    public bool AutoApprovePlans
    {
        get => _settings.AutoApprovePlans;
        set
        {
            if (_settings.AutoApprovePlans == value) return;
            _settings.AutoApprovePlans = value;
            _settingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    public TeamViewModel(BacklogService backlogService, GitService gitService,
        Func<string?> getProjectPath,
        PlannerService plannerService,
        PlanReviewerService planReviewerService,
        TeamOrchestratorService orchestratorService,
        NotificationService notificationService,
        ProjectRegistryService projectRegistry,
        SettingsService settingsService, AppSettings settings)
    {
        _backlogService = backlogService;
        _gitService = gitService;
        _getProjectPath = getProjectPath;
        _plannerService = plannerService;
        _planReviewerService = planReviewerService;
        _orchestratorService = orchestratorService;
        _notificationService = notificationService;
        _projectRegistry = projectRegistry;
        _settingsService = settingsService;
        _settings = settings;

        _backlogService.OnExternalChange += () => RunOnUI(() =>
        {
            Refresh();
            _orchestratorService.NotifyNewWork();
        });

        _isOrchestratorStopped = orchestratorService.State == OrchestratorState.Stopped;
        _orchestratorStatusText = orchestratorService.State switch
        {
            OrchestratorState.Stopped => "Stopped",
            OrchestratorState.Running => "Running",
            OrchestratorState.SoftPaused => "Pausing...",
            OrchestratorState.HardPaused => "Paused",
            OrchestratorState.WaitingForWork => "Idle",
            _ => orchestratorService.State.ToString()
        };

        DeleteFeatureCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm)
            {
                if (vm.Feature.Status is FeatureStatus.Queued or FeatureStatus.InProgress)
                {
                    MessageBox.Show(
                        $"Cannot delete '{vm.DisplayTitle}' — it is currently {vm.Feature.Status}. Cancel or wait for it to finish first.",
                        "Delete Blocked",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var result = MessageBox.Show(
                    $"Delete feature '{vm.DisplayTitle}'?",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    _plannerService.StopPlanning(vm.Id);
                    _plannerService.StopDiscussion(vm.Id);
                    _backlogService.DeleteFeature(vm.Id);
                    Refresh();
                }
            }
        });

        CancelFeatureCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm)
            {
                _plannerService.StopPlanning(vm.Id);
                _plannerService.StopDiscussion(vm.Id);
                _backlogService.MarkFeatureStatus(vm.Id, FeatureStatus.Cancelled);
                Refresh();
            }
        });

        RefreshCommand = new RelayCommand(_ => Refresh());

        AnswerQuestionCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm && !string.IsNullOrWhiteSpace(vm.AnswerText))
            {
                // Guard: discussion questions handled by dedicated UI (not the planner answer box)
                if (vm.IsDiscussionOpen || vm.IsDiscussionLoading)
                    return;

                var answer = vm.AnswerText.Trim();
                vm.AnswerText = "";

                // Atomically update status under BacklogService lock
                _backlogService.ModifyFeature(vm.Id, f =>
                {
                    f.NeedsUserInput = false;
                    f.PlannerQuestion = null;
                    f.AwaitingReason = null;
                    f.Status = FeatureStatus.Planning;
                });

                // Get a fresh snapshot for ResumePlanning
                var feature = _backlogService.GetFeatures(_getProjectPath())
                    .FirstOrDefault(f => f.Id == vm.Id);
                if (feature is null) return;

                if (!_plannerService.ResumePlanning(feature, answer))
                {
                    // Session crashed — stop old session, restart with user's answer as context
                    _plannerService.StopPlanning(feature.Id);
                    _backlogService.ModifyFeature(vm.Id, f =>
                    {
                        f.PlannerSessionId = null;
                        f.UserContext = string.IsNullOrEmpty(f.UserContext)
                            ? answer
                            : f.UserContext + "\n" + answer;
                    });
                    // Re-fetch after modification for StartPlanning
                    feature = _backlogService.GetFeatures(_getProjectPath())
                        .FirstOrDefault(f => f.Id == vm.Id);
                    if (feature is not null)
                        _plannerService.StartPlanning(feature);
                }

                Refresh();
            }
        });

        // Plan approval commands
        ApprovePlanCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm)
            {
                _planReviewerService.StopReview(vm.Id);
                _plannerService.StopDiscussion(vm.Id);
                _backlogService.ModifyFeature(vm.Id, f =>
                {
                    f.Status = FeatureStatus.PlanApproved;
                });
                Refresh();
                // PlanApproved goes to Backlog; user must "Add to Queue" for orchestrator to pick up
            }
        });

        RejectPlanCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm)
            {
                _planReviewerService.StopReview(vm.Id);
                _plannerService.StopDiscussion(vm.Id);
                _backlogService.ModifyFeature(vm.Id, f =>
                {
                    // Capture feedback before clearing
                    var feedback = f.PlanReviewComments;
                    f.Status = FeatureStatus.Planning;
                    f.PlanReviewVerdict = null;
                    f.PlanReviewComments = null;
                    f.PlanReviewSuggestions = [];
                    f.PlannerSessionId = null; // Force new session
                    // Append rejection feedback as user context so planner can improve
                    if (!string.IsNullOrEmpty(feedback))
                    {
                        f.UserContext = string.IsNullOrEmpty(f.UserContext)
                            ? $"Plan was rejected: {feedback}"
                            : f.UserContext + $"\nPlan was rejected: {feedback}";
                    }
                });

                _plannerService.StopPlanning(vm.Id);

                var feature = _backlogService.GetFeatures(_getProjectPath())
                    .FirstOrDefault(f => f.Id == vm.Id);
                if (feature is not null)
                    _plannerService.StartPlanning(feature);

                Refresh();
            }
        });

        RequestAIReviewCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm && !_planReviewerService.IsReviewing(vm.Id))
            {
                vm.IsReviewInProgress = true;
                var feature = _backlogService.GetFeatures(_getProjectPath())
                    .FirstOrDefault(f => f.Id == vm.Id);
                if (feature is not null)
                    _planReviewerService.StartReview(feature);
            }
        });

        RetryPlanningCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm && vm.Feature.Status == FeatureStatus.PlanningFailed)
            {
                var feature = _backlogService.GetFeatures(_getProjectPath())
                    .FirstOrDefault(f => f.Id == vm.Id);
                if (feature is not null)
                    TryStartPlanning(feature);
            }
        });

        // Discussion commands
        DiscussPlanCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm && vm.CanDiscuss)
            {
                var project = _getProjectPath();
                if (string.IsNullOrEmpty(project))
                {
                    vm.DiscussionError = "No project loaded";
                    return;
                }
                _plannerService.StopDiscussion(vm.Id);
                vm.IsDiscussionLoading = true;
                vm.DiscussionError = null;
                var feature = _backlogService.GetFeatures(project)
                    .FirstOrDefault(f => f.Id == vm.Id);
                if (feature is not null)
                    _plannerService.StartDiscussion(feature);
                else
                {
                    vm.IsDiscussionLoading = false;
                    vm.DiscussionError = "Feature not found";
                }
            }
        });

        SubmitDiscussionCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm && vm.IsDiscussionOpen)
            {
                var answers = vm.DiscussionQuestions
                    .Where(q => !string.IsNullOrEmpty(q.EffectiveAnswer))
                    .Select(q => (q.Question, q.EffectiveAnswer))
                    .ToList();

                if (answers.Count == 0)
                {
                    vm.DiscussionError = "Please answer at least one question";
                    return;
                }

                var project = _getProjectPath();
                if (string.IsNullOrEmpty(project))
                {
                    vm.DiscussionError = "No project loaded";
                    return;
                }

                vm.IsDiscussionOpen = false;
                vm.IsDiscussionLoading = true;
                vm.DiscussionError = null;

                var feature = _backlogService.GetFeatures(project)
                    .FirstOrDefault(f => f.Id == vm.Id);
                if (feature is not null)
                    _plannerService.SubmitDiscussionAnswers(vm.Id, feature, answers);
                else
                {
                    vm.IsDiscussionLoading = false;
                    vm.DiscussionError = "Feature not found";
                }
            }
        });

        CancelDiscussionCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm)
            {
                vm.IsDiscussionOpen = false;
                vm.IsDiscussionLoading = false;
                vm.DiscussionQuestions = [];
                vm.DiscussionError = null;
                _plannerService.StopDiscussion(vm.Id);
            }
        });

        // Backlog/Queue commands
        AddToQueueCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm && vm.Feature.Status == FeatureStatus.PlanApproved)
            {
                _backlogService.ModifyFeature(vm.Id, f =>
                {
                    f.Status = FeatureStatus.Queued;
                    f.ErrorSummary = null;
                    f.ErrorDetails = null;
                    f.ReviewDismissed = false;
                    // Reset failed phases so they can be retried
                    foreach (var phase in f.Phases.Where(ph => ph.Status == PhaseStatus.Failed))
                    {
                        phase.Status = PhaseStatus.Pending;
                        phase.ErrorMessage = null;
                        phase.StartedAt = null;
                        phase.CompletedAt = null;
                    }
                });
                Refresh();
                _orchestratorService.NotifyNewWork();
            }
        });

        ReturnToBacklogCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm
                && vm.Feature.Status is FeatureStatus.Queued or FeatureStatus.InProgress)
            {
                // Capture state before HardPause so we only auto-resume
                // if the orchestrator was actively running (not already paused by user)
                var wasRunning = _orchestratorService.IsRunning;

                if (vm.Feature.Status == FeatureStatus.InProgress)
                {
                    var result = MessageBox.Show(
                        "This feature is currently being worked on. Stop the orchestrator and return to backlog?",
                        "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes) return;

                    // HardPause kills the active session and reverts the phase to Pending
                    _orchestratorService.HardPause();
                }

                _backlogService.ModifyFeature(vm.Id, f =>
                {
                    f.Status = FeatureStatus.PlanApproved;
                });
                Refresh();

                // Only auto-resume if the orchestrator was running before we paused it
                // (don't override a deliberate user pause)
                if (wasRunning)
                {
                    var hasOtherQueued = _backlogService.GetFeatures(_getProjectPath())
                        .Any(f => f.Id != vm.Id && f.Status is FeatureStatus.Queued or FeatureStatus.InProgress);
                    if (hasOtherQueued)
                        _orchestratorService.Resume();
                }
            }
        });

        ViewHistoryCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm && vm.Feature.SessionHistoryPaths.Count > 0)
            {
                ShowSessionHistoryPopup(vm.Feature);
            }
        });

        // Completed section commands
        CommitFeatureCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm)
                ExecuteCommitFeature(vm);
        });

        ArchiveFeatureCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm)
            {
                _backlogService.ArchiveFeature(vm.Id);
                Refresh();
                RefreshArchive();
            }
        });

        CommitAllCommand = new RelayCommand(_ => ExecuteCommitAll(),
            _ => CompletedFeatures.Any(f => f.IsDone && f.AllChangedFiles.Count > 0));

        ArchiveAllCommand = new RelayCommand(_ =>
        {
            var ids = CompletedFeatures.Select(f => f.Id).ToList();
            foreach (var id in ids)
                _backlogService.ArchiveFeature(id);
            Refresh();
            RefreshArchive();
        }, _ => CompletedFeatures.Count > 0);

        AskInChatCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm)
                ExecuteAskInChat(vm);
        });

        ToggleArchiveCommand = new RelayCommand(_ =>
        {
            ShowArchive = !ShowArchive;
            if (ShowArchive)
                RefreshArchive();
        });

        // Orchestrator commands
        StartOrchestratorCommand = new RelayCommand(() =>
        {
            if (_orchestratorService.IsPaused)
                _orchestratorService.Resume();
            else
                _orchestratorService.Start();
        }, () => _orchestratorService.State is OrchestratorState.Stopped
               or OrchestratorState.SoftPaused or OrchestratorState.HardPaused);

        PauseOrchestratorCommand = new RelayCommand(
            () => _orchestratorService.SoftPause(),
            () => _orchestratorService.IsRunning);

        HardPauseOrchestratorCommand = new RelayCommand(
            () => _orchestratorService.HardPause(),
            () => _orchestratorService.State is OrchestratorState.Running
                or OrchestratorState.WaitingForWork
                or OrchestratorState.SoftPaused);

        // Subscribe to PlannerService events
        _plannerService.OnQuestionAsked += HandlePlannerQuestion;
        _plannerService.OnPlanReady += HandlePlanReady;
        _plannerService.OnPlannerError += HandlePlannerError;
        _plannerService.OnDiscussionReady += HandleDiscussionReady;
        _plannerService.OnDiscussionError += HandleDiscussionError;

        // Subscribe to PlanReviewerService events
        _planReviewerService.OnReviewComplete += HandlePlanReviewComplete;
        _planReviewerService.OnReviewError += HandlePlanReviewError;

        // Subscribe to Orchestrator events
        _orchestratorService.OnLog += HandleOrchestratorLog;
        _orchestratorService.OnStateChanged += HandleOrchestratorStateChanged;
        _orchestratorService.OnSoftPauseRequested += HandleSoftPauseRequested;
        _orchestratorService.OnPhaseCompleted += HandleOrchestratorPhaseCompleted;
        _orchestratorService.OnError += HandleOrchestratorError;
        _orchestratorService.OnHealthSnapshot += HandleHealthSnapshot;
        _orchestratorService.OnActiveTaskChanged += HandleActiveTaskChanged;
        _orchestratorService.OnDevTextDelta += HandleDevTextDelta;
        _orchestratorService.OnReviewTextDelta += HandleReviewTextDelta;

        // Structured events for rich team chat
        _teamChatAssembler = new ChatMessageAssembler(TeamChatMessages);
        _orchestratorService.OnDevTextBlockStart += HandleDevTextBlockStart;
        _orchestratorService.OnDevThinkingDelta += HandleDevThinkingDelta;
        _orchestratorService.OnDevToolUseStarted += HandleDevToolUseStarted;
        _orchestratorService.OnDevToolResult += HandleDevToolResult;
        _orchestratorService.OnDevCompleted += HandleDevCompleted;
        _orchestratorService.OnDevError += HandleDevError;
        _orchestratorService.OnReviewCompleted += HandleReviewCompleted;
        _orchestratorService.OnReviewError += HandleReviewError;
        _orchestratorService.OnPhaseStarted += HandlePhaseStarted;
    }

    public string ProjectName
    {
        get => _projectName;
        set => SetProperty(ref _projectName, value);
    }

    public int PendingTaskCount => PlanningFeatures.Count + AwaitingApprovalFeatures.Count
        + BacklogFeatures.Count + QueuedFeatures.Count;

    /// <summary>
    /// Attempt to start planning for a PlanningFailed feature.
    /// </summary>
    private void TryStartPlanning(BacklogFeature feature)
    {
        if (feature.Status != FeatureStatus.PlanningFailed) return;
        if (_plannerService.IsPlanning(feature.Id)) return;

        _backlogService.ModifyFeature(feature.Id, f =>
        {
            f.Status = FeatureStatus.Planning;
            f.RejectionReason = null;
        });

        var updated = _backlogService.GetFeatures(_getProjectPath())
            .FirstOrDefault(f => f.Id == feature.Id);
        if (updated is not null)
            _plannerService.StartPlanning(updated);
        Refresh();
    }

    /// <summary>
    /// Start planning for pending features (called on startup/refresh if needed).
    /// </summary>
    public void TryStartPendingPlanning()
    {
        var projectPath = _getProjectPath();
        if (string.IsNullOrEmpty(projectPath)) return;

        var failedFeatures = _backlogService.GetFeatures(projectPath)
            .Where(f => f.Status == FeatureStatus.PlanningFailed)
            .OrderBy(f => f.Priority)
            .ToList();

        foreach (var feature in failedFeatures)
            TryStartPlanning(feature);
    }

    private void HandlePlannerQuestion(string featureId, string question)
    {
        var sessionId = _plannerService.GetSessionId(featureId);

        RunOnUI(() =>
        {
            _backlogService.ModifyFeature(featureId, f =>
            {
                if (!string.IsNullOrEmpty(sessionId))
                    f.PlannerSessionId = sessionId;
                f.Status = FeatureStatus.AwaitingUser;
                f.AwaitingReason = AwaitingUserReason.PlanningQuestion;
                f.NeedsUserInput = true;
                f.PlannerQuestion = question;
            });
            Refresh();
            _notificationService.NotifyIfInactive();
        });
    }

    private void HandlePlanReady(string featureId, string title, string? sessionId,
        List<BacklogPhase> phases)
    {
        RunOnUI(() =>
        {
            var autoApprove = _settings.AutoApprovePlans;
            _backlogService.ModifyFeature(featureId, f =>
            {
                if (!string.IsNullOrEmpty(title))
                    f.Title = title;
                f.Phases = phases;
                // Auto-approve: skip backlog, go straight to queue
                f.Status = autoApprove ? FeatureStatus.Queued : FeatureStatus.PlanReady;
                f.NeedsUserInput = false;
                f.PlannerQuestion = null;
                if (sessionId is not null)
                    f.PlannerSessionId = sessionId;
                // Clear stale review data — previous review was for the old plan
                f.PlanReviewVerdict = null;
                f.PlanReviewComments = null;
                f.PlanReviewSuggestions = [];
                f.ReviewDismissed = false;
            });

            // Close discussion panel if open (refinement completed)
            var vm = FindFeatureVM(featureId);
            if (vm != null)
            {
                vm.IsDiscussionOpen = false;
                vm.IsDiscussionLoading = false;
                vm.DiscussionQuestions = [];
                vm.DiscussionError = null;
                vm.IsReviewInProgress = false;
            }

            Refresh();

            if (autoApprove)
                _orchestratorService.NotifyNewWork();
            else
                _notificationService.NotifyIfInactive();
        });
    }

    private void HandlePlannerError(string featureId, string error)
    {
        RunOnUI(() =>
        {
            _backlogService.ModifyFeature(featureId, f =>
            {
                f.Status = FeatureStatus.PlanningFailed;
                f.NeedsUserInput = false;
                f.RejectionReason = $"Planning failed: {error}";
                f.PlannerQuestion = null;
            });
            Refresh();
        });
    }

    private void HandleDiscussionReady(string featureId, PlanDiscussionResult result)
    {
        RunOnUI(() =>
        {
            var vm = FindFeatureVM(featureId);
            if (vm != null && vm.IsDiscussionLoading)
            {
                vm.DiscussionQuestions = result.Questions
                    .Select(q => new DiscussionQuestionVM
                    {
                        Index = q.Index,
                        GroupId = $"{featureId}_{q.Index}",
                        Question = q.Question,
                        SuggestedAnswers = q.SuggestedAnswers
                    })
                    .ToList();
                vm.IsDiscussionOpen = true;
                vm.IsDiscussionLoading = false;
                vm.DiscussionError = null;
                _notificationService.NotifyIfInactive();
            }
        });
    }

    private void HandleDiscussionError(string featureId, string error)
    {
        RunOnUI(() =>
        {
            var vm = FindFeatureVM(featureId);
            if (vm != null && (vm.IsDiscussionLoading || vm.IsDiscussionOpen))
            {
                vm.IsDiscussionLoading = false;
                vm.IsDiscussionOpen = false;
                vm.DiscussionQuestions = [];
                vm.DiscussionError = error;
            }
        });
    }

    private BacklogFeatureVM? FindFeatureVM(string featureId)
    {
        return AwaitingApprovalFeatures.FirstOrDefault(f => f.Id == featureId)
            ?? PlanningFeatures.FirstOrDefault(f => f.Id == featureId)
            ?? BacklogFeatures.FirstOrDefault(f => f.Id == featureId)
            ?? QueuedFeatures.FirstOrDefault(f => f.Id == featureId)
            ?? CompletedFeatures.FirstOrDefault(f => f.Id == featureId);
    }

    private void HandlePlanReviewComplete(string featureId, PlanReviewResult result)
    {
        RunOnUI(() =>
        {
            _backlogService.ModifyFeature(featureId, f =>
            {
                f.PlanReviewVerdict = result.Verdict;
                f.PlanReviewComments = result.Comments;
                f.PlanReviewSuggestions = result.Suggestions;
            });
            Refresh();
            _notificationService.NotifyIfInactive();
        });
    }

    private void HandlePlanReviewError(string featureId, string error)
    {
        RunOnUI(() =>
        {
            _backlogService.ModifyFeature(featureId, f =>
            {
                f.PlanReviewVerdict = "error";
                f.PlanReviewComments = $"Review failed: {error}";
            });
            Refresh();
        });
    }

    public void Refresh()
    {
        // Preserve user-typed answers before clearing
        var savedAnswers = new Dictionary<string, string>();
        foreach (var vm in PlanningFeatures)
        {
            if (!string.IsNullOrEmpty(vm.AnswerText))
                savedAnswers[vm.Id] = vm.AnswerText;
        }

        // Preserve discussion state before clearing
        var savedDiscussions = new Dictionary<string, (bool IsLoading, bool IsOpen,
            List<DiscussionQuestionVM> Questions, string? Error)>();
        foreach (var vm in AwaitingApprovalFeatures)
        {
            if (vm.IsDiscussionLoading || vm.IsDiscussionOpen || vm.HasDiscussionError)
                savedDiscussions[vm.Id] = (vm.IsDiscussionLoading, vm.IsDiscussionOpen,
                    vm.DiscussionQuestions, vm.DiscussionError);
        }

        var projectPath = _getProjectPath();

        PlanningFeatures.Clear();
        AwaitingApprovalFeatures.Clear();
        BacklogFeatures.Clear();
        QueuedFeatures.Clear();
        CompletedFeatures.Clear();

        if (string.IsNullOrEmpty(projectPath))
        {
            UpdateCounts();
            return;
        }

        var features = _backlogService.GetFeatures(projectPath);

        foreach (var f in features.OrderBy(f => f.Priority).ThenBy(f => f.CreatedAt))
        {
            var vm = new BacklogFeatureVM(f);
            switch (f.Status)
            {
                case FeatureStatus.Planning:
                case FeatureStatus.PlanningFailed:
                    PlanningFeatures.Add(vm);
                    break;
                case FeatureStatus.AwaitingUser:
                    vm.IsExpanded = true;
                    if (savedAnswers.TryGetValue(f.Id, out var savedText))
                        vm.AnswerText = savedText;
                    PlanningFeatures.Add(vm);
                    break;
                case FeatureStatus.PlanReady:
                    vm.IsReviewInProgress = _planReviewerService.IsReviewing(f.Id);
                    vm.IsExpanded = true;
                    if (savedDiscussions.TryGetValue(f.Id, out var disc))
                    {
                        vm.IsDiscussionLoading = disc.IsLoading;
                        vm.IsDiscussionOpen = disc.IsOpen;
                        vm.DiscussionQuestions = disc.Questions;
                        vm.DiscussionError = disc.Error;
                    }
                    AwaitingApprovalFeatures.Add(vm);
                    break;
                case FeatureStatus.PlanApproved:
                    BacklogFeatures.Add(vm);
                    break;
                case FeatureStatus.Queued:
                case FeatureStatus.InProgress:
                    vm.IsExpanded = f.Status == FeatureStatus.InProgress;
                    QueuedFeatures.Add(vm);
                    break;
                case FeatureStatus.Done:
                case FeatureStatus.Cancelled:
                    CompletedFeatures.Add(vm);
                    break;
            }
        }

        // Mark the actively developing backlog item
        var activeId = _orchestratorService.GetActiveFeatureId();
        foreach (var vm in BacklogFeatures)
            vm.IsActiveDeveloping = vm.Id == activeId;

        UpdateCounts();

        if (_showArchive)
            RefreshArchive();
    }

    public bool IsEmpty => PlanningCount + ApprovalCount
                         + BacklogCount + QueuedCount + CompletedCount == 0;

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(PlanningCount));
        OnPropertyChanged(nameof(ApprovalCount));
        OnPropertyChanged(nameof(BacklogCount));
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(PendingTaskCount));
    }

    private const int MaxLogLines = 200;

    private void HandleOrchestratorLog(string message)
    {
        RunOnUI(() =>
        {
            var log = _orchestratorLog + $"[{DateTime.Now:HH:mm:ss}] {message}\n";

            // Cap log at MaxLogLines to prevent unbounded memory growth
            var lines = log.Split('\n');
            if (lines.Length > MaxLogLines)
                log = string.Join('\n', lines[^MaxLogLines..]);

            OrchestratorLog = log;
        });
    }

    private void HandleOrchestratorStateChanged(OrchestratorState state)
    {
        RunOnUI(() =>
        {
            OrchestratorStatusText = state switch
            {
                OrchestratorState.Stopped => "Stopped",
                OrchestratorState.Running => _orchestratorService.IsSoftPauseRequested
                    ? "Pausing after current task..."
                    : "Running",
                OrchestratorState.SoftPaused => "Paused",
                OrchestratorState.HardPaused => "Paused",
                OrchestratorState.WaitingForWork => "Idle",
                _ => state.ToString()
            };
            IsOrchestratorStopped = state == OrchestratorState.Stopped;

            StartOrchestratorCommand.RaiseCanExecuteChanged();
            PauseOrchestratorCommand.RaiseCanExecuteChanged();
            HardPauseOrchestratorCommand.RaiseCanExecuteChanged();
        });
    }

    private void HandleSoftPauseRequested(bool requested)
    {
        RunOnUI(() =>
        {
            if (requested && _orchestratorService.State == OrchestratorState.Running)
                OrchestratorStatusText = "Pausing after current task...";
            else if (!requested && _orchestratorService.State == OrchestratorState.Running)
                OrchestratorStatusText = "Running";
        });
    }

    private void HandleOrchestratorPhaseCompleted(string featureId, string phaseId, PhaseStatus status)
    {
        RunOnUI(() =>
        {
            Refresh();

            if (status == PhaseStatus.Done)
            {
                // Check if entire feature is done
                var features = _backlogService.GetFeatures(_getProjectPath());
                var feature = features.FirstOrDefault(f => f.Id == featureId);
                if (feature?.Phases.All(p => p.Status == PhaseStatus.Done) == true)
                {
                    SystemSounds.Asterisk.Play();
                    _notificationService.NotifyIfInactive();
                }
            }
            else if (status == PhaseStatus.Failed)
            {
                _notificationService.NotifyIfInactive();
            }
        });
    }

    private void HandleOrchestratorError(string error)
    {
        RunOnUI(() =>
        {
            HandleOrchestratorLog($"ERROR: {error}");
            _notificationService.NotifyIfInactive();
        });
    }

    private void HandleHealthSnapshot(IReadOnlyList<SessionHealthInfo> items)
    {
        RunOnUI(() =>
        {
            SessionHealthItems.Clear();
            foreach (var item in items)
                SessionHealthItems.Add(item);
            OnPropertyChanged(nameof(HasActiveSession));
            OnPropertyChanged(nameof(ActiveElapsed));
            OnPropertyChanged(nameof(ActiveRole));
            OnPropertyChanged(nameof(ActiveReviewRound));
            OnPropertyChanged(nameof(ActiveIdleSeconds));
            OnPropertyChanged(nameof(ActiveIdleText));
            OnPropertyChanged(nameof(IsIdleWarning));
        });
    }

    // --- Active task & live chat ---

    private const int MaxChatTextLength = 50_000; // ~50KB cap
    private bool _showingReviewChat;

    private void HandleActiveTaskChanged(string? featureId)
    {
        RunOnUI(() =>
        {
            ActiveDevelopingFeatureId = featureId;
            _devChatText = "";
            _reviewChatText = "";
            _devChatBuffer.Clear();
            _reviewChatBuffer.Clear();
            _chatFlushTimer?.Stop();
            _chatFlushTimer = null;
            _chatBufferDirty = false;
            OnPropertyChanged(nameof(DevChatText));
            OnPropertyChanged(nameof(ReviewChatText));
            DevChatLabel = "Development";
            _showingReviewChat = false;
            _reviewBubbleStarted = false;
            OnPropertyChanged(nameof(LiveChatText));

            TeamChatMessages.Clear();
            _teamChatAssembler.Reset();

            if (featureId == null)
            {
                SessionHealthItems.Clear();
                OnPropertyChanged(nameof(HasActiveSession));
            }

            // Update IsActiveDeveloping on backlog items
            foreach (var vm in BacklogFeatures)
                vm.IsActiveDeveloping = vm.Id == featureId;
        });
    }

    private void HandleDevTextDelta(string text)
    {
        RunOnUI(() =>
        {
            _devChatBuffer.Append(text);
            _showingReviewChat = false;
            EnsureChatFlushTimer();

            // Feed structured assembler (text blocks are handled via OnDevTextBlockStart)
            _teamChatAssembler.HandleTextDelta(text);
        });
    }

    private bool _reviewBubbleStarted;

    private void HandleReviewTextDelta(string text)
    {
        RunOnUI(() =>
        {
            _reviewChatBuffer.Append(text);
            _showingReviewChat = true;
            EnsureChatFlushTimer();

            // Start review bubble on first review text delta
            if (!_reviewBubbleStarted)
            {
                _reviewBubbleStarted = true;
                _teamChatAssembler.AddSystemMessage("── Review ──");
                _teamChatAssembler.BeginAssistantMessage("Reviewer");
            }
            _teamChatAssembler.HandleTextDelta(text);
        });
    }

    // ── Structured event handlers for rich team chat ──

    private void HandleDevTextBlockStart() =>
        RunOnUI(() =>
        {
            EnsureDevAssistantMessage();
            _teamChatAssembler.HandleTextBlockStart();
        });

    private void HandleDevThinkingDelta(string text) =>
        RunOnUI(() =>
        {
            EnsureDevAssistantMessage();
            _teamChatAssembler.HandleThinkingDelta(text);
        });

    private void HandleDevToolUseStarted(string name, string id, string input) =>
        RunOnUI(() =>
        {
            EnsureDevAssistantMessage();
            _teamChatAssembler.HandleToolUseStarted(name, id, input);
        });

    private void HandleDevToolResult(string name, string id, string content) =>
        RunOnUI(() =>
        {
            EnsureDevAssistantMessage();
            _teamChatAssembler.HandleToolResult(name, id, content);
        });

    private void HandleDevCompleted() =>
        RunOnUI(() => _teamChatAssembler.HandleCompleted());

    private void HandleDevError(string error) =>
        RunOnUI(() => _teamChatAssembler.HandleError(error));

    private void HandleReviewCompleted() =>
        RunOnUI(() =>
        {
            _teamChatAssembler.HandleCompleted();
            _reviewBubbleStarted = false;
        });

    private void HandleReviewError(string error) =>
        RunOnUI(() => _teamChatAssembler.HandleError(error));

    private void HandlePhaseStarted(string featureId, string phaseId) => RunOnUI(() =>
    {
        _reviewBubbleStarted = false;
        _teamChatAssembler.AddSystemMessage($"── Development: {phaseId} ──");
        _teamChatAssembler.BeginAssistantMessage();
    });

    /// <summary>
    /// Defensively ensure an assistant message exists for dev output.
    /// Handles fix cycles and health-check restarts where RaisePhaseStarted is not called.
    /// </summary>
    private void EnsureDevAssistantMessage()
    {
        if (_teamChatAssembler.CurrentMessage is null)
        {
            _teamChatAssembler.AddSystemMessage("── Fix ──");
            _teamChatAssembler.BeginAssistantMessage();
        }
    }

    private void EnsureChatFlushTimer()
    {
        _chatBufferDirty = true;
        if (_chatFlushTimer != null) return;
        _chatFlushTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _chatFlushTimer.Tick += (_, _) => FlushChatBuffer();
        _chatFlushTimer.Start();
    }

    private void FlushChatBuffer()
    {
        if (!_chatBufferDirty)
        {
            _chatFlushTimer?.Stop();
            _chatFlushTimer = null;
            return;
        }

        _chatBufferDirty = false;

        if (_devChatBuffer.Length > 0)
        {
            _devChatText += _devChatBuffer.ToString();
            _devChatBuffer.Clear();
            if (_devChatText.Length > MaxChatTextLength)
                _devChatText = _devChatText[^MaxChatTextLength..];
            OnPropertyChanged(nameof(DevChatText));
            DevChatLabel = "Development";
        }

        if (_reviewChatBuffer.Length > 0)
        {
            _reviewChatText += _reviewChatBuffer.ToString();
            _reviewChatBuffer.Clear();
            if (_reviewChatText.Length > MaxChatTextLength)
                _reviewChatText = _reviewChatText[^MaxChatTextLength..];
            OnPropertyChanged(nameof(ReviewChatText));
            DevChatLabel = "Review";
        }

        OnPropertyChanged(nameof(LiveChatText));
    }

    /// <summary>
    /// Returns the latest chat text — dev during Development/FixingIssues, review during Review.
    /// Phase tracked locally via _showingReviewChat to avoid lock acquisition on every delta.
    /// </summary>
    public string LiveChatText => _showingReviewChat ? _reviewChatText : _devChatText;

    private static async void ShowSessionHistoryPopup(BacklogFeature feature)
    {
        var paths = feature.SessionHistoryPaths.ToList();
        var title = feature.Title ?? feature.RawIdea;

        var text = await Task.Run(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Session History: {title}");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();

            foreach (var path in paths)
            {
                if (System.IO.File.Exists(path))
                {
                    try
                    {
                        const int maxFilePreview = 100_000;
                        var content = System.IO.File.ReadAllText(path);
                        if (content.Length > maxFilePreview)
                            content = content[..maxFilePreview] + "\n\n[truncated — open file for full output]";
                        sb.AppendLine(content);
                        sb.AppendLine();
                    }
                    catch
                    {
                        sb.AppendLine($"[Could not read: {path}]");
                        sb.AppendLine();
                    }
                }
                else
                {
                    sb.AppendLine($"[File not found: {path}]");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        });

        var window = new Window
        {
            Title = $"Session History — {title}",
            Width = 800,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1e, 0x1e, 0x2e)),
            Content = new System.Windows.Controls.TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x1e, 0x1e, 0x2e)),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xcd, 0xd6, 0xf4)),
                CaretBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xcd, 0xd6, 0xf4)),
                BorderThickness = new Thickness(0),
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Code,Consolas,Courier New"),
                FontSize = 12,
                Padding = new Thickness(12),
                Margin = new Thickness(0)
            }
        };
        window.ShowDialog();
    }

    // --- Completed section methods ---

    private void ExecuteCommitFeature(BacklogFeatureVM vm)
    {
        var files = vm.AllChangedFiles;
        if (files.Count == 0)
        {
            MessageBox.Show("No changed files to commit.", "Commit",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var title = vm.Feature.Title ?? vm.Feature.RawIdea;
        var prefix = title.StartsWith("fix", StringComparison.OrdinalIgnoreCase) ? "fix" : "feat";
        var message = $"{prefix}: {title}";

        var (success, result) = _gitService.CommitFiles(files, message, _getProjectPath());
        if (success)
        {
            MessageBox.Show($"Committed: {result}\n{message}", "Commit Successful",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Commit failed: {result}", "Commit Failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExecuteCommitAll()
    {
        var doneFeatures = CompletedFeatures
            .Where(f => f.IsDone && f.AllChangedFiles.Count > 0)
            .ToList();

        if (doneFeatures.Count == 0)
        {
            MessageBox.Show("No completed features with changed files.", "Commit All",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var projectPath = _getProjectPath();
        var results = new StringBuilder();
        var committed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var vm in doneFeatures)
        {
            // Skip files already committed by a previous feature in this batch
            var files = vm.AllChangedFiles
                .Where(f => !committed.Contains(f))
                .ToList();

            if (files.Count == 0)
            {
                results.AppendLine($"[skip] {vm.DisplayTitle} — files already committed");
                continue;
            }

            var title = vm.Feature.Title ?? vm.Feature.RawIdea;
            var prefix = title.StartsWith("fix", StringComparison.OrdinalIgnoreCase) ? "fix" : "feat";
            var message = $"{prefix}: {title}";

            var (success, result) = _gitService.CommitFiles(files, message, projectPath);
            results.AppendLine(success
                ? $"[ok] {vm.DisplayTitle} — {result}"
                : $"[fail] {vm.DisplayTitle} — {result}");

            if (success)
            {
                foreach (var f in files)
                    committed.Add(f);
            }
            else
            {
                // Unstage this feature's files to prevent leaking into next feature's commit
                foreach (var f in files)
                    _gitService.RunGit($"reset HEAD -- \"{f}\"", projectPath);
            }
        }

        MessageBox.Show(results.ToString(), "Commit All Results",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExecuteAskInChat(BacklogFeatureVM vm)
    {
        var sb = new StringBuilder();
        var title = vm.Feature.Title ?? vm.Feature.RawIdea;
        sb.AppendLine($"I need help with a completed team task: \"{title}\"");
        sb.AppendLine();

        if (vm.Feature.Phases.Count > 0)
        {
            sb.AppendLine("Phases:");
            foreach (var phase in vm.Feature.Phases.OrderBy(p => p.Order))
            {
                var status = phase.Status == PhaseStatus.Done ? "done" : phase.Status.ToString();
                sb.AppendLine($"  {phase.Order}. {phase.Title} [{status}]");
                if (!string.IsNullOrEmpty(phase.Summary))
                    sb.AppendLine($"     Summary: {phase.Summary}");
            }
            sb.AppendLine();
        }

        var files = vm.AllChangedFiles;
        if (files.Count > 0)
        {
            sb.AppendLine("Changed files:");
            foreach (var f in files)
                sb.AppendLine($"  - {f}");
        }

        if (vm.HasUserActions)
        {
            sb.AppendLine();
            sb.AppendLine("User actions required:");
            sb.AppendLine(vm.UserActionsText);
        }

        OnAskInChat?.Invoke(sb.ToString());
    }

    private void RefreshArchive()
    {
        ArchivedFeatures.Clear();
        var projectPath = _getProjectPath();
        if (string.IsNullOrEmpty(projectPath)) return;

        var archived = _backlogService.GetArchivedFeatures(projectPath);
        foreach (var f in archived.OrderByDescending(f => f.ArchivedAt ?? f.UpdatedAt))
            ArchivedFeatures.Add(new BacklogFeatureVM(f));

        OnPropertyChanged(nameof(ArchivedCount));
    }

    public void Dispose()
    {
        _plannerService.OnQuestionAsked -= HandlePlannerQuestion;
        _plannerService.OnPlanReady -= HandlePlanReady;
        _plannerService.OnPlannerError -= HandlePlannerError;
        _plannerService.OnDiscussionReady -= HandleDiscussionReady;
        _plannerService.OnDiscussionError -= HandleDiscussionError;
        _plannerService.StopAll();

        _planReviewerService.OnReviewComplete -= HandlePlanReviewComplete;
        _planReviewerService.OnReviewError -= HandlePlanReviewError;
        _planReviewerService.StopAll();

        _orchestratorService.OnLog -= HandleOrchestratorLog;
        _orchestratorService.OnStateChanged -= HandleOrchestratorStateChanged;
        _orchestratorService.OnSoftPauseRequested -= HandleSoftPauseRequested;
        _orchestratorService.OnPhaseCompleted -= HandleOrchestratorPhaseCompleted;
        _orchestratorService.OnError -= HandleOrchestratorError;
        _orchestratorService.OnHealthSnapshot -= HandleHealthSnapshot;
        _orchestratorService.OnActiveTaskChanged -= HandleActiveTaskChanged;
        _orchestratorService.OnDevTextDelta -= HandleDevTextDelta;
        _orchestratorService.OnReviewTextDelta -= HandleReviewTextDelta;
        _orchestratorService.OnDevTextBlockStart -= HandleDevTextBlockStart;
        _orchestratorService.OnDevThinkingDelta -= HandleDevThinkingDelta;
        _orchestratorService.OnDevToolUseStarted -= HandleDevToolUseStarted;
        _orchestratorService.OnDevToolResult -= HandleDevToolResult;
        _orchestratorService.OnDevCompleted -= HandleDevCompleted;
        _orchestratorService.OnDevError -= HandleDevError;
        _orchestratorService.OnReviewCompleted -= HandleReviewCompleted;
        _orchestratorService.OnReviewError -= HandleReviewError;
        _orchestratorService.OnPhaseStarted -= HandlePhaseStarted;
        _orchestratorService.Dispose();

        _chatFlushTimer?.Stop();
        _chatFlushTimer = null;
    }
}
