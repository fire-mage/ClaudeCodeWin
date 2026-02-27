using System.Collections.ObjectModel;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public class ReviewPanelViewModel : ViewModelBase
{
    private bool _isOpen;
    private bool _isReviewing;
    private ReviewStatus _status = ReviewStatus.Idle;
    private int _currentRound;
    private int _maxRounds;
    private string _judgeInput = "";
    private ReviewMessageViewModel? _currentStreamingMessage;

    public ObservableCollection<ReviewMessageViewModel> Messages { get; } = [];

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public bool IsReviewing
    {
        get => _isReviewing;
        set => SetProperty(ref _isReviewing, value);
    }

    public ReviewStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ShowJudgeInput));
                OnPropertyChanged(nameof(ShowActions));
                OnPropertyChanged(nameof(IsResolved));
            }
        }
    }

    public int CurrentRound
    {
        get => _currentRound;
        set
        {
            if (SetProperty(ref _currentRound, value))
                OnPropertyChanged(nameof(RoundText));
        }
    }

    public int MaxRounds
    {
        get => _maxRounds;
        set
        {
            if (SetProperty(ref _maxRounds, value))
                OnPropertyChanged(nameof(RoundText));
        }
    }

    public string JudgeInput
    {
        get => _judgeInput;
        set => SetProperty(ref _judgeInput, value);
    }

    public string RoundText => $"Round {_currentRound}/{_maxRounds}";

    public string StatusText => _status switch
    {
        ReviewStatus.Idle => "",
        ReviewStatus.InProgress => "Review in progress...",
        ReviewStatus.Consensus => "Consensus reached",
        ReviewStatus.Disagreement => "Disagreement",
        ReviewStatus.Escalated => "Waiting for Judge",
        ReviewStatus.Dismissed => "Dismissed",
        _ => ""
    };

    public bool ShowJudgeInput => _status == ReviewStatus.Escalated;
    public bool ShowActions => _status == ReviewStatus.Consensus;
    public bool IsResolved => _status is ReviewStatus.Consensus or ReviewStatus.Dismissed;

    public RelayCommand CloseCommand { get; }
    public RelayCommand DismissCommand { get; }
    public RelayCommand SendJudgeVerdictCommand { get; }
    public RelayCommand ApplyFixesCommand { get; }

    // Events for MainViewModel to handle
    public event Action? OnCloseRequested;
    public event Action<string>? OnJudgeVerdictSubmitted;
    public event Action<string>? OnApplyFixesRequested;

    public ReviewPanelViewModel()
    {
        CloseCommand = new RelayCommand(() =>
        {
            IsOpen = false;
            OnCloseRequested?.Invoke();
        });

        DismissCommand = new RelayCommand(() =>
        {
            Status = ReviewStatus.Dismissed;
            IsReviewing = false;
        });

        SendJudgeVerdictCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(JudgeInput)) return;
            var verdict = JudgeInput.Trim();
            JudgeInput = "";
            OnJudgeVerdictSubmitted?.Invoke(verdict);
        });

        ApplyFixesCommand = new RelayCommand(() =>
        {
            // Collect all reviewer suggestions into a prompt
            var suggestions = string.Join("\n\n",
                Messages
                    .Where(m => m.Role == ReviewRole.Reviewer)
                    .Select(m => m.Text));
            OnApplyFixesRequested?.Invoke(suggestions);
        });
    }

    /// <summary>
    /// Begin streaming a new message from a participant.
    /// </summary>
    public void StartMessage(ReviewRole role)
    {
        _currentStreamingMessage = new ReviewMessageViewModel(role, "", isStreaming: true);
        Messages.Add(_currentStreamingMessage);
        IsReviewing = true;
    }

    /// <summary>
    /// Append text to the currently streaming message.
    /// </summary>
    public void AppendText(string text)
    {
        if (_currentStreamingMessage is not null)
            _currentStreamingMessage.Text += text;
    }

    /// <summary>
    /// Complete the current streaming message.
    /// </summary>
    public void CompleteMessage(string fullText)
    {
        if (_currentStreamingMessage is not null)
        {
            _currentStreamingMessage.Text = fullText;
            _currentStreamingMessage.IsStreaming = false;
            _currentStreamingMessage = null;
        }
        IsReviewing = false;
    }

    /// <summary>
    /// Add a complete (non-streaming) message.
    /// </summary>
    public void AddMessage(ReviewRole role, string text)
    {
        Messages.Add(new ReviewMessageViewModel(role, text));
    }

    /// <summary>
    /// Reset the panel for a new review.
    /// </summary>
    public void Reset()
    {
        Messages.Clear();
        Status = ReviewStatus.Idle;
        CurrentRound = 0;
        MaxRounds = 0;
        IsReviewing = false;
        JudgeInput = "";
        _currentStreamingMessage = null;
    }
}
