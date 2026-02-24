using System.Collections.ObjectModel;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public class FinalizeActionsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly Func<string?> _getWorkingDirectory;

    private bool _showTaskSuggestion;
    private bool _showFinalizeActionsLabel;
    private bool _hasCompletedTask;
    private bool _finalizeLabelBlinking;
    private int _finalizeCountdown;
    private string _projectName = "";
    private System.Windows.Threading.DispatcherTimer? _taskSuggestionTimer;
    private System.Windows.Threading.DispatcherTimer? _blinkTimer;

    /// <summary>Set by View to animate popup collapse instead of instant hide.</summary>
    public Action? OnFinalizeCollapse { get; set; }

    /// <summary>Raised when user clicks a commit suggestion.</summary>
    public event Action<string>? OnCommitRequested;

    /// <summary>Raised when user clicks a task suggestion.</summary>
    public event Action<TaskDefinition>? OnRunTaskRequested;

    public ObservableCollection<TaskSuggestionItem> SuggestedTasks { get; } = [];

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

    public string ProjectName
    {
        get => _projectName;
        set => SetProperty(ref _projectName, value);
    }

    public RelayCommand RunSuggestedTaskCommand { get; }
    public RelayCommand CloseFinalizePopupCommand { get; }
    public RelayCommand OpenFinalizeActionsCommand { get; }
    public RelayCommand DontSuggestForProjectCommand { get; }

    public FinalizeActionsViewModel(SettingsService settingsService, AppSettings settings,
        Func<string?> getWorkingDirectory)
    {
        _settingsService = settingsService;
        _settings = settings;
        _getWorkingDirectory = getWorkingDirectory;

        RunSuggestedTaskCommand = new RelayCommand(p =>
        {
            if (p is TaskSuggestionItem item && !item.IsCompleted)
            {
                item.IsCompleted = true;
                item.CompletedStatusText = item.IsCommit ? "Committed" : $"Ran {item.Label}";
                CollapseToFinalizeLabel();

                if (item.IsCommit)
                    OnCommitRequested?.Invoke("Review the current git changes (staged and unstaged), create a commit with an appropriate message, and push to the remote repository.");
                else if (item.Task is not null)
                    OnRunTaskRequested?.Invoke(item.Task);
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
            var workDir = _getWorkingDirectory();
            if (!string.IsNullOrEmpty(workDir))
            {
                var normalized = workDir.NormalizePath();
                if (!_settings.TaskSuggestionDismissedProjects.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.TaskSuggestionDismissedProjects.Add(normalized);
                    _settingsService.Save(_settings);
                }
            }
        });
    }

    public void StopTaskSuggestionTimer()
    {
        _taskSuggestionTimer?.Stop();
        _taskSuggestionTimer = null;
    }

    public void StopBlinkTimer()
    {
        _blinkTimer?.Stop();
        _blinkTimer = null;
        FinalizeLabelBlinking = false;
    }

    public void StartAutoCollapseTimer()
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

    public void CollapseToFinalizeLabel()
    {
        StopTaskSuggestionTimer();
        FinalizeCountdown = 0;
        var showLabel = SuggestedTasks.Count > 0;

        if (ShowTaskSuggestion && OnFinalizeCollapse is not null)
        {
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
            if (elapsed >= 10)
                StopBlinkTimer();
        };
        _blinkTimer.Start();
    }
}
