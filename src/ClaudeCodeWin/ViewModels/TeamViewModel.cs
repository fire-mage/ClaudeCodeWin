using System.Collections.ObjectModel;
using System.Media;
using System.Windows;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public class TeamViewModel : ViewModelBase, IDisposable
{
    private readonly BacklogService _backlogService;
    private readonly Func<string?> _getProjectPath;
    private readonly PlannerService _plannerService;
    private readonly TeamOrchestratorService _orchestratorService;
    private readonly NotificationService _notificationService;
    private readonly ManagerService _managerService;
    private string _addIdeaText = "";
    private SubTab? _teamTab;
    private string _orchestratorStatusText = "Stopped";
    private string _orchestratorLog = "";
    private string _managerChatLog = "";
    private string _managerInputText = "";
    private string _managerResponse = "";
    private bool _isManagerActive;

    public ObservableCollection<BacklogFeatureVM> InProgressFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> AwaitingUserFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> PlannedFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> RawFeatures { get; } = [];
    public ObservableCollection<BacklogFeatureVM> CompletedFeatures { get; } = [];
    public ObservableCollection<SessionHealthInfo> SessionHealthItems { get; } = [];
    public bool HasActiveSession => SessionHealthItems.Count > 0;

    public string AddIdeaText
    {
        get => _addIdeaText;
        set
        {
            if (SetProperty(ref _addIdeaText, value))
                AddIdeaCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasInProgress => InProgressFeatures.Count > 0;
    public bool HasAwaitingUser => AwaitingUserFeatures.Count > 0;
    public bool HasPlanned => PlannedFeatures.Count > 0;
    public bool HasRaw => RawFeatures.Count > 0;
    public bool HasCompleted => CompletedFeatures.Count > 0;

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
    public RelayCommand DeleteFeatureCommand { get; }
    public RelayCommand CancelFeatureCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand AnswerQuestionCommand { get; }
    public RelayCommand StartOrchestratorCommand { get; }
    public RelayCommand PauseOrchestratorCommand { get; }
    public RelayCommand HardPauseOrchestratorCommand { get; }
    public RelayCommand StopOrchestratorCommand { get; }
    public RelayCommand StartManagerCommand { get; }
    public RelayCommand StopManagerCommand { get; }
    public RelayCommand SendManagerMessageCommand { get; }

    public TeamViewModel(BacklogService backlogService, Func<string?> getProjectPath,
        PlannerService plannerService, TeamOrchestratorService orchestratorService,
        NotificationService notificationService, ManagerService managerService)
    {
        _backlogService = backlogService;
        _getProjectPath = getProjectPath;
        _plannerService = plannerService;
        _orchestratorService = orchestratorService;
        _notificationService = notificationService;
        _managerService = managerService;

        AddIdeaCommand = new RelayCommand(ExecuteAddIdea,
            () => !string.IsNullOrWhiteSpace(_addIdeaText)
                  && !string.IsNullOrEmpty(_getProjectPath()));

        DeleteFeatureCommand = new RelayCommand(p =>
        {
            if (p is BacklogFeatureVM vm)
            {
                var result = MessageBox.Show(
                    $"Delete feature '{vm.DisplayTitle}'?",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
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
                var answer = vm.AnswerText.Trim();
                vm.AnswerText = "";

                // Atomically update status under BacklogService lock
                _backlogService.ModifyFeature(vm.Id, f =>
                {
                    f.NeedsUserInput = false;
                    f.PlannerQuestion = null;
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

        // Orchestrator commands
        StartOrchestratorCommand = new RelayCommand(() =>
        {
            if (_orchestratorService.IsPaused)
                _orchestratorService.Resume();
            else
                _orchestratorService.Start();
        }, () => _orchestratorService.State is OrchestratorState.Stopped
               or OrchestratorState.SoftPaused or OrchestratorState.HardPaused
               or OrchestratorState.WaitingForWork);

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

        // Subscribe to PlannerService events
        _plannerService.OnQuestionAsked += HandlePlannerQuestion;
        _plannerService.OnPlanReady += HandlePlanReady;
        _plannerService.OnPlannerError += HandlePlannerError;

        // Subscribe to Orchestrator events
        _orchestratorService.OnLog += HandleOrchestratorLog;
        _orchestratorService.OnStateChanged += HandleOrchestratorStateChanged;
        _orchestratorService.OnPhaseCompleted += HandleOrchestratorPhaseCompleted;
        _orchestratorService.OnError += HandleOrchestratorError;
        _orchestratorService.OnHealthSnapshot += HandleHealthSnapshot;

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

        TryStartPlanning(feature);
    }

    /// <summary>
    /// Attempt to start planning for a Raw feature.
    /// </summary>
    private void TryStartPlanning(BacklogFeature feature)
    {
        if (feature.Status != FeatureStatus.Raw) return;
        if (_plannerService.IsPlanning(feature.Id)) return;

        _backlogService.ModifyFeature(feature.Id, f => f.Status = FeatureStatus.Planning);

        // Re-fetch the updated feature for StartPlanning
        var updated = _backlogService.GetFeatures(_getProjectPath())
            .FirstOrDefault(f => f.Id == feature.Id);
        if (updated is not null)
            _plannerService.StartPlanning(updated);
        Refresh();
    }

    /// <summary>
    /// Start planning for all Raw features (called on startup/refresh if needed).
    /// </summary>
    public void TryStartPendingPlanning()
    {
        var projectPath = _getProjectPath();
        if (string.IsNullOrEmpty(projectPath)) return;

        var rawFeatures = _backlogService.GetFeatures(projectPath)
            .Where(f => f.Status == FeatureStatus.Raw)
            .OrderBy(f => f.Priority)
            .ToList();

        foreach (var feature in rawFeatures)
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
            _backlogService.ModifyFeature(featureId, f =>
            {
                f.Title = title;
                f.Phases = phases;
                f.Status = FeatureStatus.Planned;
                f.NeedsUserInput = false;
                f.PlannerQuestion = null;
                f.PlannerSessionId = sessionId;
            });
            Refresh();
        });
    }

    private void HandlePlannerError(string featureId, string error)
    {
        RunOnUI(() =>
        {
            _backlogService.ModifyFeature(featureId, f =>
            {
                f.Status = FeatureStatus.Raw;
                f.NeedsUserInput = false;
                f.PlannerQuestion = $"Planning failed: {error}";
            });
            Refresh();
        });
    }

    public void Refresh()
    {
        // Preserve user-typed answers before clearing
        var savedAnswers = new Dictionary<string, string>();
        foreach (var vm in AwaitingUserFeatures)
        {
            if (!string.IsNullOrEmpty(vm.AnswerText))
                savedAnswers[vm.Id] = vm.AnswerText;
        }

        var projectPath = _getProjectPath();

        InProgressFeatures.Clear();
        AwaitingUserFeatures.Clear();
        PlannedFeatures.Clear();
        RawFeatures.Clear();
        CompletedFeatures.Clear();

        if (string.IsNullOrEmpty(projectPath))
        {
            UpdateGroupVisibility();
            return;
        }

        var features = _backlogService.GetFeatures(projectPath);

        foreach (var f in features.OrderBy(f => f.Priority).ThenBy(f => f.CreatedAt))
        {
            var vm = new BacklogFeatureVM(f);
            switch (f.Status)
            {
                case FeatureStatus.InProgress:
                    vm.IsExpanded = true;
                    InProgressFeatures.Add(vm);
                    break;
                case FeatureStatus.AwaitingUser:
                    vm.IsExpanded = true;
                    // Restore previously typed answer
                    if (savedAnswers.TryGetValue(f.Id, out var savedText))
                        vm.AnswerText = savedText;
                    AwaitingUserFeatures.Add(vm);
                    break;
                case FeatureStatus.Planned:
                    PlannedFeatures.Add(vm);
                    break;
                case FeatureStatus.Raw:
                case FeatureStatus.Planning:
                    RawFeatures.Add(vm);
                    break;
                case FeatureStatus.Done:
                case FeatureStatus.Cancelled:
                    CompletedFeatures.Add(vm);
                    break;
            }
        }

        UpdateGroupVisibility();
    }

    public bool IsEmpty => !HasInProgress && !HasAwaitingUser && !HasPlanned && !HasRaw && !HasCompleted;

    private void UpdateGroupVisibility()
    {
        if (_teamTab != null)
            _teamTab.BadgeCount = AwaitingUserFeatures.Count;

        OnPropertyChanged(nameof(HasInProgress));
        OnPropertyChanged(nameof(HasAwaitingUser));
        OnPropertyChanged(nameof(HasPlanned));
        OnPropertyChanged(nameof(HasRaw));
        OnPropertyChanged(nameof(HasCompleted));
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

    public void Dispose()
    {
        _plannerService.OnQuestionAsked -= HandlePlannerQuestion;
        _plannerService.OnPlanReady -= HandlePlanReady;
        _plannerService.OnPlannerError -= HandlePlannerError;
        _plannerService.StopAll();

        _orchestratorService.OnLog -= HandleOrchestratorLog;
        _orchestratorService.OnStateChanged -= HandleOrchestratorStateChanged;
        _orchestratorService.OnPhaseCompleted -= HandleOrchestratorPhaseCompleted;
        _orchestratorService.OnError -= HandleOrchestratorError;
        _orchestratorService.OnHealthSnapshot -= HandleHealthSnapshot;
        _orchestratorService.Dispose();

        _managerService.OnTextDelta -= HandleManagerTextDelta;
        _managerService.OnCompleted -= HandleManagerCompleted;
        _managerService.OnActionParsed -= HandleManagerAction;
        _managerService.OnError -= HandleManagerError;
        _managerService.StopSession();
    }
}
