using System.Collections.ObjectModel;
using System.Media;
using System.Text;
using System.Text.RegularExpressions;
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
    private readonly AnalyzerService _analyzerService;
    private readonly PlanReviewerService _planReviewerService;
    private readonly TeamOrchestratorService _orchestratorService;
    private readonly NotificationService _notificationService;
    private readonly ManagerService _managerService;
    private readonly IdeasStorageService _ideasStorageService;
    private readonly ProjectRegistryService _projectRegistry;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private string _addIdeaText = "";
    private SubTab? _teamTab;
    private string _orchestratorStatusText = "Stopped";
    private string _orchestratorLog = "";
    private string _managerChatLog = "";
    private string _managerInputText = "";
    private string _managerResponse = "";
    private bool _isManagerActive;
    private bool _showArchive;

    // Live chat viewer state
    private string? _activeDevelopingFeatureId;
    private string _devChatText = "";
    private string _reviewChatText = "";
    private bool _isDevChatVisible;
    private string _devChatLabel = "Development";
    private readonly StringBuilder _devChatBuffer = new();
    private readonly StringBuilder _reviewChatBuffer = new();
    private System.Windows.Threading.DispatcherTimer? _chatFlushTimer;
    private bool _chatBufferDirty;

    // Pipeline collections (6 sections, Ideas is IdeasText)
    public ObservableCollection<BacklogFeatureVM> AnalyzingFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> PlanningFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> AwaitingApprovalFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> BacklogFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> QueuedFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> CompletedFeatures { get; } = [];
    public ObservableCollection<SessionHealthInfo> SessionHealthItems { get; } = [];
    public bool HasActiveSession => SessionHealthItems.Count > 0;
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
        private set => SetProperty(ref _activeDevelopingFeatureId, value);
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

    public bool IsDevChatVisible
    {
        get => _isDevChatVisible;
        set => SetProperty(ref _isDevChatVisible, value);
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

    private string _ideasText = "";
    private System.Windows.Threading.DispatcherTimer? _ideasSaveTimer;
    private string? _ideasPendingText;
    private string? _ideasPendingProject;
    private string? _ideasLoadedForProject;
    private string _ideasSaveStatus = "";

    public string IdeasText
    {
        get => _ideasText;
        set
        {
            if (SetProperty(ref _ideasText, value))
            {
                IdeasSaveStatus = "Unsaved";
                DebounceSaveIdeas();
                SubmitIdeasCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string IdeasSaveStatus
    {
        get => _ideasSaveStatus;
        private set => SetProperty(ref _ideasSaveStatus, value);
    }

    private void DebounceSaveIdeas()
    {
        _ideasPendingText = _ideasText;
        _ideasPendingProject = _ideasLoadedForProject;
        if (_ideasSaveTimer == null)
        {
            _ideasSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _ideasSaveTimer.Tick += OnIdeasSaveTimerTick;
        }
        _ideasSaveTimer.Stop();
        _ideasSaveTimer.Start();
    }

    private void OnIdeasSaveTimerTick(object? sender, EventArgs e)
    {
        _ideasSaveTimer?.Stop();
        if (!string.IsNullOrEmpty(_ideasPendingProject) && _ideasPendingText != null)
        {
            _ideasStorageService.Save(_ideasPendingProject, _ideasPendingText);
            if (_ideasText == _ideasPendingText)
                IdeasSaveStatus = "Saved \u2713";
        }
    }

    private void SaveIdeasNow()
    {
        _ideasSaveTimer?.Stop();
        _ideasSaveTimer = null;
        var project = _ideasLoadedForProject;
        if (!string.IsNullOrEmpty(project))
        {
            _ideasStorageService.Save(project, _ideasText);
            IdeasSaveStatus = "Saved \u2713";
        }
    }

    private void LoadIdeas()
    {
        var project = _getProjectPath();
        if (string.IsNullOrEmpty(project)) return;
        if (string.Equals(_ideasLoadedForProject, project, StringComparison.OrdinalIgnoreCase)) return;

        // Flush pending save for the previous project before switching
        if (_ideasSaveTimer != null)
        {
            _ideasSaveTimer.Stop();
            _ideasSaveTimer = null;
            if (!string.IsNullOrEmpty(_ideasLoadedForProject))
                _ideasStorageService.Save(_ideasLoadedForProject, _ideasText);
        }

        _ideasLoadedForProject = project;
        var doc = _ideasStorageService.Load(project);
        _ideasText = doc.Text;
        IdeasSaveStatus = string.IsNullOrEmpty(_ideasText) ? "" : "Saved \u2713";
        OnPropertyChanged(nameof(IdeasText));
        SubmitIdeasCommand.RaiseCanExecuteChanged();
    }

    public string AddIdeaText
    {
        get => _addIdeaText;
        set
        {
            if (SetProperty(ref _addIdeaText, value))
                AddIdeaCommand.RaiseCanExecuteChanged();
        }
    }

    // Section counts for badge binding
    public int AnalyzingCount => AnalyzingFeatures.Count;
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

    public string OrchestratorLog
    {
        get => _orchestratorLog;
        set => SetProperty(ref _orchestratorLog, value);
    }

    public string ManagerChatLog
    {
        get => _managerChatLog;
        set => SetProperty(ref _managerChatLog, value);
    }

    public string ManagerInputText
    {
        get => _managerInputText;
        set
        {
            if (SetProperty(ref _managerInputText, value))
                SendManagerMessageCommand.RaiseCanExecuteChanged();
        }
    }

    public string ManagerResponse
    {
        get => _managerResponse;
        set => SetProperty(ref _managerResponse, value);
    }

    public bool IsManagerActive
    {
        get => _isManagerActive;
        set
        {
            if (SetProperty(ref _isManagerActive, value))
            {
                StartManagerCommand.RaiseCanExecuteChanged();
                StopManagerCommand.RaiseCanExecuteChanged();
                SendManagerMessageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand AddIdeaCommand { get; }
    public RelayCommand SubmitIdeasCommand { get; }
    public RelayCommand SaveIdeasCommand { get; }
    public RelayCommand ClearIdeasCommand { get; }
    public RelayCommand DeleteFeatureCommand { get; }
    public RelayCommand CancelFeatureCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand AnswerQuestionCommand { get; }
    public RelayCommand ApproveAnalysisCommand { get; }
    public RelayCommand RejectAnalysisCommand { get; }
    public RelayCommand SendToPlanningCommand { get; }
    public RelayCommand AnswerAnalysisCommand { get; }
    public RelayCommand StartOrchestratorCommand { get; }
    public RelayCommand PauseOrchestratorCommand { get; }
    public RelayCommand HardPauseOrchestratorCommand { get; }
    public RelayCommand StopOrchestratorCommand { get; }
    public RelayCommand ApprovePlanCommand { get; }
    public RelayCommand RejectPlanCommand { get; }
    public RelayCommand RequestAIReviewCommand { get; }
    public RelayCommand RetryPlanningCommand { get; }
    public RelayCommand AddToQueueCommand { get; }
    public RelayCommand ReturnToBacklogCommand { get; }
    public RelayCommand ViewHistoryCommand { get; }
    public RelayCommand StartManagerCommand { get; }
    public RelayCommand StopManagerCommand { get; }
    public RelayCommand SendManagerMessageCommand { get; }
    public RelayCommand CommitFeatureCommand { get; }
    public RelayCommand ArchiveFeatureCommand { get; }
    public RelayCommand CommitAllCommand { get; }
    public RelayCommand ArchiveAllCommand { get; }
    public RelayCommand AskInChatCommand { get; }
    public RelayCommand ToggleArchiveCommand { get; }

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
        PlannerService plannerService, AnalyzerService analyzerService,
        PlanReviewerService planReviewerService,
        TeamOrchestratorService orchestratorService,
        NotificationService notificationService, ManagerService managerService,
        IdeasStorageService ideasStorageService, ProjectRegistryService projectRegistry,
        SettingsService settingsService, AppSettings settings)
    {
        _backlogService = backlogService;
        _gitService = gitService;
        _getProjectPath = getProjectPath;
        _plannerService = plannerService;
        _analyzerService = analyzerService;
        _planReviewerService = planReviewerService;
        _orchestratorService = orchestratorService;
        _notificationService = notificationService;
        _managerService = managerService;
        _ideasStorageService = ideasStorageService;
        _projectRegistry = projectRegistry;
        _settingsService = settingsService;
        _settings = settings;

        AddIdeaCommand = new RelayCommand(ExecuteAddIdea,
            () => !string.IsNullOrWhiteSpace(_addIdeaText)
                  && !string.IsNullOrEmpty(_getProjectPath()));

        SubmitIdeasCommand = new RelayCommand(ExecuteSubmitIdeas,
            () => !string.IsNullOrWhiteSpace(_ideasText)
                  && !string.IsNullOrEmpty(_ideasLoadedForProject));

        SaveIdeasCommand = new RelayCommand(SaveIdeasNow,
            () => !string.IsNullOrEmpty(_ideasLoadedForProject));

        ClearIdeasCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(_ideasText)) return;
            var result = MessageBox.Show("Clear all ideas?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
            _ideasText = "";
            OnPropertyChanged(nameof(IdeasText));
            SubmitIdeasCommand.RaiseCanExecuteChanged();
            SaveIdeasNow();
        });

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
                    _analyzerService.StopAnalysis(vm.Id);
                    _plannerService.StopPlanning(vm.Id);
                    _backlogService.DeleteFeature(vm.Id);
                    Refresh();
                }
            }
        });

        CancelFeatureCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm)
            {
                _analyzerService.StopAnalysis(vm.Id);
                _plannerService.StopPlanning(vm.Id);
                _backlogService.MarkFeatureStatus(vm.Id, FeatureStatus.Cancelled);
                Refresh();
            }
        });

        RefreshCommand = new RelayCommand(_ => Refresh());

        AnswerQuestionCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm && !string.IsNullOrWhiteSpace(vm.AnswerText))
            {
                // Guard: analysis questions will be handled by AnalyzerService (Phase 4)
                if (vm.Feature.AwaitingReason == AwaitingUserReason.AnalysisQuestion)
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

        // Analysis commands
        ApproveAnalysisCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm)
            {
                _analyzerService.StopAnalysis(vm.Id);
                _backlogService.ModifyFeature(vm.Id, f =>
                {
                    f.Status = FeatureStatus.AnalysisDone;
                    f.NeedsUserInput = false;
                    f.PlannerQuestion = null;
                    f.AwaitingReason = null;
                });
                Refresh();
            }
        });

        RejectAnalysisCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm)
            {
                _analyzerService.StopAnalysis(vm.Id);
                _backlogService.ModifyFeature(vm.Id, f =>
                {
                    f.Status = FeatureStatus.AnalysisRejected;
                    f.RejectionReason = "Rejected by user";
                    f.NeedsUserInput = false;
                    f.PlannerQuestion = null;
                    f.AwaitingReason = null;
                });
                Refresh();
            }
        });

        SendToPlanningCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm && vm.Feature.Status == FeatureStatus.AnalysisDone)
            {
                _backlogService.ModifyFeature(vm.Id, f =>
                {
                    f.Status = FeatureStatus.Planning;
                });

                var feature = _backlogService.GetFeatures(_getProjectPath())
                    .FirstOrDefault(f => f.Id == vm.Id);
                if (feature is not null)
                    _plannerService.StartPlanning(feature);
                Refresh();
            }
        });

        AnswerAnalysisCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm && !string.IsNullOrWhiteSpace(vm.AnswerText)
                && vm.Feature.AwaitingReason == AwaitingUserReason.AnalysisQuestion)
            {
                var answer = vm.AnswerText.Trim();
                vm.AnswerText = "";

                _backlogService.ModifyFeature(vm.Id, f =>
                {
                    f.NeedsUserInput = false;
                    f.PlannerQuestion = null;
                    f.AwaitingReason = null;
                    f.Status = FeatureStatus.Analyzing;
                });

                var feature = _backlogService.GetFeatures(_getProjectPath())
                    .FirstOrDefault(f => f.Id == vm.Id);
                if (feature is null) return;

                if (!_analyzerService.ResumeAnalysis(feature, answer))
                {
                    _analyzerService.StopAnalysis(feature.Id);
                    _backlogService.ModifyFeature(vm.Id, f =>
                    {
                        f.AnalysisSessionId = null;
                        f.UserContext = string.IsNullOrEmpty(f.UserContext)
                            ? answer
                            : f.UserContext + "\n" + answer;
                    });
                    feature = _backlogService.GetFeatures(_getProjectPath())
                        .FirstOrDefault(f => f.Id == vm.Id);
                    if (feature is not null)
                        _analyzerService.StartAnalysis(feature);
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

        StopOrchestratorCommand = new RelayCommand(
            () => _orchestratorService.Stop(),
            () => _orchestratorService.State != OrchestratorState.Stopped);

        // Subscribe to AnalyzerService events
        _analyzerService.OnAnalysisComplete += HandleAnalysisComplete;
        _analyzerService.OnQuestionAsked += HandleAnalysisQuestion;
        _analyzerService.OnAnalysisError += HandleAnalysisError;

        // Subscribe to PlannerService events
        _plannerService.OnQuestionAsked += HandlePlannerQuestion;
        _plannerService.OnPlanReady += HandlePlanReady;
        _plannerService.OnPlannerError += HandlePlannerError;

        // Subscribe to PlanReviewerService events
        _planReviewerService.OnReviewComplete += HandlePlanReviewComplete;
        _planReviewerService.OnReviewError += HandlePlanReviewError;

        // Subscribe to Orchestrator events
        _orchestratorService.OnLog += HandleOrchestratorLog;
        _orchestratorService.OnStateChanged += HandleOrchestratorStateChanged;
        _orchestratorService.OnPhaseCompleted += HandleOrchestratorPhaseCompleted;
        _orchestratorService.OnError += HandleOrchestratorError;
        _orchestratorService.OnHealthSnapshot += HandleHealthSnapshot;
        _orchestratorService.OnActiveTaskChanged += HandleActiveTaskChanged;
        _orchestratorService.OnDevTextDelta += HandleDevTextDelta;
        _orchestratorService.OnReviewTextDelta += HandleReviewTextDelta;

        // Manager commands
        StartManagerCommand = new RelayCommand(ExecuteStartManager, () => !_isManagerActive);
        StopManagerCommand = new RelayCommand(ExecuteStopManager, () => _isManagerActive);
        SendManagerMessageCommand = new RelayCommand(ExecuteSendManagerMessage,
            () => _isManagerActive && !_managerService.IsBusy
                  && !string.IsNullOrWhiteSpace(_managerInputText));

        // Subscribe to Manager events
        _managerService.OnTextDelta += HandleManagerTextDelta;
        _managerService.OnCompleted += HandleManagerCompleted;
        _managerService.OnActionParsed += HandleManagerAction;
        _managerService.OnError += HandleManagerError;
    }

    public void SetTeamTab(SubTab tab) => _teamTab = tab;

    private void ExecuteAddIdea()
    {
        var project = _getProjectPath();
        if (string.IsNullOrEmpty(project) || string.IsNullOrWhiteSpace(_addIdeaText))
            return;

        var feature = _backlogService.AddFeature(project, _addIdeaText.Trim());
        AddIdeaText = "";
        Refresh();

        TryStartAnalysis(feature);
    }

    private void ExecuteSubmitIdeas()
    {
        var project = _ideasLoadedForProject;
        if (string.IsNullOrEmpty(project) || string.IsNullOrWhiteSpace(_ideasText))
            return;

        var ideas = ParseIdeas(_ideasText);
        if (ideas.Count == 0) return;

        foreach (var idea in ideas)
            _backlogService.AddFeature(project, idea);

        _ideasText = "";
        OnPropertyChanged(nameof(IdeasText));
        SubmitIdeasCommand.RaiseCanExecuteChanged();
        SaveIdeasNow();
        Refresh();
        TryStartPendingPlanning();
    }

    private static List<string> ParseIdeas(string text)
    {
        // Normalize Windows line endings — WPF TextBox produces \r\n
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        var ideas = new List<string>();

        // Check if text uses list markers (- , * , 1. 2. etc.)
        var hasListMarkers = Regex.IsMatch(text, @"(?m)^[\s]*[-*]\s|^[\s]*\d+\.\s");

        if (hasListMarkers)
        {
            // Split by list markers: lines starting with "- ", "* ", or "1. "
            var lines = text.Split('\n');
            // Start with empty string so preamble text before first marker becomes an idea
            var current = "";

            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (Regex.IsMatch(trimmed, @"^[-*]\s|^\d+\.\s"))
                {
                    // Save previous item (preamble or prior list item)
                    var cleaned = current.Trim();
                    if (cleaned.Length > 0)
                        ideas.Add(cleaned);
                    // Strip the marker
                    current = Regex.Replace(trimmed, @"^[-*]\s|^\d+\.\s", "");
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    // Continuation line
                    current += " " + trimmed;
                }
            }

            // Don't forget the last item
            var lastCleaned = current.Trim();
            if (lastCleaned.Length > 0)
                ideas.Add(lastCleaned);
        }
        else
        {
            // Split by double newline
            var parts = Regex.Split(text, @"\n\s*\n");
            foreach (var part in parts)
            {
                var cleaned = part.Trim();
                if (cleaned.Length > 0)
                    ideas.Add(cleaned);
            }
        }

        // Fallback: if no split produced results, treat entire text as one idea
        if (ideas.Count == 0)
        {
            var cleaned = text.Trim();
            if (cleaned.Length > 0)
                ideas.Add(cleaned);
        }

        return ideas;
    }

    /// <summary>
    /// Attempt to start analysis for an Analyzing feature.
    /// </summary>
    private void TryStartAnalysis(BacklogFeature feature)
    {
        if (feature.Status != FeatureStatus.Analyzing) return;
        if (_analyzerService.IsAnalyzing(feature.Id)) return;

        EnsureAnalyzerConfigured();
        _analyzerService.StartAnalysis(feature);
        Refresh();
    }

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
    /// Start analysis/planning for all pending features (called on startup/refresh if needed).
    /// </summary>
    public void TryStartPendingPlanning()
    {
        var projectPath = _getProjectPath();
        if (string.IsNullOrEmpty(projectPath)) return;

        var analyzingFeatures = _backlogService.GetFeatures(projectPath)
            .Where(f => f.Status == FeatureStatus.Analyzing)
            .OrderBy(f => f.Priority)
            .ToList();

        foreach (var feature in analyzingFeatures)
            TryStartAnalysis(feature);

        var failedFeatures = _backlogService.GetFeatures(projectPath)
            .Where(f => f.Status == FeatureStatus.PlanningFailed)
            .OrderBy(f => f.Priority)
            .ToList();

        foreach (var feature in failedFeatures)
            TryStartPlanning(feature);
    }

    private void EnsureAnalyzerConfigured()
    {
        var projectPath = _getProjectPath();
        var prompt = TeamPrompts.BuildAnalyzerSystemPrompt(projectPath, _projectRegistry.Projects);
        _analyzerService.UpdateSystemPrompt(prompt);
    }

    // --- Analysis event handlers ---

    private void HandleAnalysisComplete(string featureId, AnalysisResult result)
    {
        RunOnUI(() =>
        {
            _backlogService.ModifyFeature(featureId, f =>
            {
                f.AnalysisResult = result.Summary;
                f.AnalysisSessionId = result.SessionId;
                f.AffectedProjects = result.AffectedProjects;
                f.NeedsUserInput = false;
                f.PlannerQuestion = null;

                if (!string.IsNullOrEmpty(result.Title))
                    f.Title = result.Title;

                switch (result.Verdict.ToLowerInvariant())
                {
                    case "approve":
                        f.Status = FeatureStatus.AnalysisDone;
                        break;
                    case "reject":
                        f.Status = FeatureStatus.AnalysisRejected;
                        f.RejectionReason = result.Reason ?? "Rejected by analyzer";
                        break;
                    case "needs_discussion":
                    default:
                        f.Status = FeatureStatus.AwaitingUser;
                        f.AwaitingReason = AwaitingUserReason.AnalysisQuestion;
                        f.NeedsUserInput = true;
                        f.PlannerQuestion = result.Reason ?? result.Summary;
                        break;
                }
            });
            Refresh();
            _notificationService.NotifyIfInactive();
        });
    }

    private void HandleAnalysisQuestion(string featureId, string question)
    {
        var sessionId = _analyzerService.GetSessionId(featureId);

        RunOnUI(() =>
        {
            _backlogService.ModifyFeature(featureId, f =>
            {
                if (!string.IsNullOrEmpty(sessionId))
                    f.AnalysisSessionId = sessionId;
                f.Status = FeatureStatus.AwaitingUser;
                f.AwaitingReason = AwaitingUserReason.AnalysisQuestion;
                f.NeedsUserInput = true;
                f.PlannerQuestion = question;
            });
            Refresh();
            _notificationService.NotifyIfInactive();
        });
    }

    private void HandleAnalysisError(string featureId, string error)
    {
        RunOnUI(() =>
        {
            _backlogService.ModifyFeature(featureId, f =>
            {
                f.Status = FeatureStatus.AnalysisRejected;
                f.RejectionReason = $"Analysis failed: {error}";
                f.NeedsUserInput = false;
                f.PlannerQuestion = null;
            });
            Refresh();
        });
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
                f.Title = title;
                f.Phases = phases;
                // Auto-approve: skip backlog, go straight to queue
                f.Status = autoApprove ? FeatureStatus.Queued : FeatureStatus.PlanReady;
                f.NeedsUserInput = false;
                f.PlannerQuestion = null;
                f.PlannerSessionId = sessionId;
            });
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
        LoadIdeas();

        // Preserve user-typed answers before clearing
        var savedAnswers = new Dictionary<string, string>();
        foreach (var vm in PlanningFeatures.Concat(AnalyzingFeatures))
        {
            if (!string.IsNullOrEmpty(vm.AnswerText))
                savedAnswers[vm.Id] = vm.AnswerText;
        }

        var projectPath = _getProjectPath();

        AnalyzingFeatures.Clear();
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
                case FeatureStatus.Analyzing:
                case FeatureStatus.AnalysisDone:
                case FeatureStatus.AnalysisRejected:
                    AnalyzingFeatures.Add(vm);
                    break;
                case FeatureStatus.Planning:
                case FeatureStatus.PlanningFailed:
                    PlanningFeatures.Add(vm);
                    break;
                case FeatureStatus.AwaitingUser:
                    vm.IsExpanded = true;
                    if (savedAnswers.TryGetValue(f.Id, out var savedText))
                        vm.AnswerText = savedText;
                    // Route by awaiting reason
                    if (f.AwaitingReason == AwaitingUserReason.AnalysisQuestion)
                        AnalyzingFeatures.Add(vm);
                    else
                        PlanningFeatures.Add(vm);
                    break;
                case FeatureStatus.PlanReady:
                    vm.IsReviewInProgress = _planReviewerService.IsReviewing(f.Id);
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

    public bool IsEmpty => AnalyzingCount + PlanningCount + ApprovalCount
                         + BacklogCount + QueuedCount + CompletedCount == 0;

    private void UpdateCounts()
    {
        if (_teamTab != null)
        {
            // Badge = items needing user attention (include completed features with user actions)
            var attention = PlanningFeatures.Count(f => f.NeedsUserInput)
                          + AnalyzingFeatures.Count(f => f.NeedsUserInput)
                          + AwaitingApprovalFeatures.Count
                          + CompletedFeatures.Count(f => f.HasUserActions);
            _teamTab.BadgeCount = attention;
        }

        OnPropertyChanged(nameof(AnalyzingCount));
        OnPropertyChanged(nameof(PlanningCount));
        OnPropertyChanged(nameof(ApprovalCount));
        OnPropertyChanged(nameof(BacklogCount));
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(IsEmpty));
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
                OrchestratorState.Running => "Running",
                OrchestratorState.SoftPaused => "Pausing...",
                OrchestratorState.HardPaused => "Paused",
                OrchestratorState.WaitingForWork => "Idle",
                _ => state.ToString()
            };

            StartOrchestratorCommand.RaiseCanExecuteChanged();
            PauseOrchestratorCommand.RaiseCanExecuteChanged();
            HardPauseOrchestratorCommand.RaiseCanExecuteChanged();
            StopOrchestratorCommand.RaiseCanExecuteChanged();
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
            OnPropertyChanged(nameof(LiveChatText));

            if (featureId == null)
            {
                SessionHealthItems.Clear();
                OnPropertyChanged(nameof(HasActiveSession));
                IsDevChatVisible = false;
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
        });
    }

    private void HandleReviewTextDelta(string text)
    {
        RunOnUI(() =>
        {
            _reviewChatBuffer.Append(text);
            _showingReviewChat = true;
            EnsureChatFlushTimer();
        });
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

    // --- Manager ---

    private void ExecuteStartManager()
    {
        var projectPath = _getProjectPath();
        var features = string.IsNullOrEmpty(projectPath)
            ? new List<BacklogFeature>()
            : _backlogService.GetFeatures(projectPath);

        var prompt = TeamPrompts.BuildManagerSystemPrompt(features, OrchestratorStatusText);
        _managerService.StartSession(prompt);
        IsManagerActive = true;
        AppendManagerChat("System", "Manager session started.");
    }

    private void ExecuteStopManager()
    {
        _managerService.StopSession();
        IsManagerActive = false;
        AppendManagerChat("System", "Manager session stopped.");
    }

    private void ExecuteSendManagerMessage()
    {
        var text = _managerInputText.Trim();
        if (string.IsNullOrEmpty(text)) return;

        ManagerInputText = "";
        AppendManagerChat("You", text);
        ManagerResponse = "";
        _managerService.SendMessage(text);
        SendManagerMessageCommand.RaiseCanExecuteChanged();
    }

    private void HandleManagerTextDelta(string text)
    {
        RunOnUI(() => ManagerResponse += text);
    }

    private void HandleManagerCompleted(string fullResponse)
    {
        RunOnUI(() =>
        {
            // Strip action blocks from display text
            var displayText = StripActionBlocks(fullResponse);
            AppendManagerChat("Manager", displayText.Trim());
            ManagerResponse = "";
            SendManagerMessageCommand.RaiseCanExecuteChanged();
        });
    }

    private void HandleManagerAction(ManagerAction action)
    {
        RunOnUI(() =>
        {
            switch (action.Type)
            {
                case ManagerActionType.Reprioritize when action.FeatureId != null && action.NewPriority.HasValue:
                {
                    var msg = $"Manager suggests reprioritizing [{action.FeatureId}] to P{action.NewPriority}.";
                    if (action.Reason != null) msg += $"\nReason: {action.Reason}";
                    var confirm = MessageBox.Show(msg + "\n\nApply?",
                        "Manager Action", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (confirm != MessageBoxResult.Yes)
                    {
                        AppendManagerChat("Action", $"Reprioritize [{action.FeatureId}] — declined by user.");
                        break;
                    }
                    _backlogService.ModifyFeature(action.FeatureId, f => f.Priority = action.NewPriority.Value);
                    AppendManagerChat("Action",
                        $"Reprioritized [{action.FeatureId}] to P{action.NewPriority}" +
                        (action.Reason != null ? $" — {action.Reason}" : ""));
                    Refresh();
                    break;
                }

                case ManagerActionType.Cancel when action.FeatureId != null:
                {
                    var msg = $"Manager suggests cancelling [{action.FeatureId}].";
                    if (action.Reason != null) msg += $"\nReason: {action.Reason}";
                    var confirm = MessageBox.Show(msg + "\n\nApply?",
                        "Manager Action", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (confirm != MessageBoxResult.Yes)
                    {
                        AppendManagerChat("Action", $"Cancel [{action.FeatureId}] — declined by user.");
                        break;
                    }
                    _analyzerService.StopAnalysis(action.FeatureId);
                    _plannerService.StopPlanning(action.FeatureId);
                    _backlogService.MarkFeatureStatus(action.FeatureId, FeatureStatus.Cancelled);
                    AppendManagerChat("Action",
                        $"Cancelled [{action.FeatureId}]" +
                        (action.Reason != null ? $" — {action.Reason}" : ""));
                    Refresh();
                    break;
                }

                case ManagerActionType.Suggest:
                    AppendManagerChat("Suggestion", action.Reason ?? "No details provided.");
                    break;
            }
        });
    }

    private void HandleManagerError(string error)
    {
        RunOnUI(() =>
        {
            IsManagerActive = false;
            AppendManagerChat("Error", error);
            SendManagerMessageCommand.RaiseCanExecuteChanged();
            _notificationService.NotifyIfInactive();
        });
    }

    private void AppendManagerChat(string role, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var log = _managerChatLog + $"[{timestamp}] {role}: {message}\n";

        var lines = log.Split('\n');
        if (lines.Length > MaxLogLines)
            log = string.Join('\n', lines[^MaxLogLines..]);

        ManagerChatLog = log;
    }

    private static string StripActionBlocks(string text)
    {
        var result = text;
        while (true)
        {
            var start = result.IndexOf("```action", StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            var end = result.IndexOf("```", start + 9, StringComparison.Ordinal);
            if (end < 0) break;
            result = result[..start] + result[(end + 3)..];
        }
        return result;
    }

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
        _analyzerService.OnAnalysisComplete -= HandleAnalysisComplete;
        _analyzerService.OnQuestionAsked -= HandleAnalysisQuestion;
        _analyzerService.OnAnalysisError -= HandleAnalysisError;
        _analyzerService.StopAll();

        _plannerService.OnQuestionAsked -= HandlePlannerQuestion;
        _plannerService.OnPlanReady -= HandlePlanReady;
        _plannerService.OnPlannerError -= HandlePlannerError;
        _plannerService.StopAll();

        _planReviewerService.OnReviewComplete -= HandlePlanReviewComplete;
        _planReviewerService.OnReviewError -= HandlePlanReviewError;
        _planReviewerService.StopAll();

        _orchestratorService.OnLog -= HandleOrchestratorLog;
        _orchestratorService.OnStateChanged -= HandleOrchestratorStateChanged;
        _orchestratorService.OnPhaseCompleted -= HandleOrchestratorPhaseCompleted;
        _orchestratorService.OnError -= HandleOrchestratorError;
        _orchestratorService.OnHealthSnapshot -= HandleHealthSnapshot;
        _orchestratorService.OnActiveTaskChanged -= HandleActiveTaskChanged;
        _orchestratorService.OnDevTextDelta -= HandleDevTextDelta;
        _orchestratorService.OnReviewTextDelta -= HandleReviewTextDelta;
        _orchestratorService.Dispose();

        _managerService.OnTextDelta -= HandleManagerTextDelta;
        _managerService.OnCompleted -= HandleManagerCompleted;
        _managerService.OnActionParsed -= HandleManagerAction;
        _managerService.OnError -= HandleManagerError;
        _managerService.StopSession();

        _chatFlushTimer?.Stop();
        _chatFlushTimer = null;
        _ideasSaveTimer?.Stop();
        _ideasSaveTimer = null;
        if (!string.IsNullOrEmpty(_ideasLoadedForProject))
            _ideasStorageService.Save(_ideasLoadedForProject, _ideasText);
    }
}
