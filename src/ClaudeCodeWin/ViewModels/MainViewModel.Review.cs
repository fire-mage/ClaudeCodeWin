using System.Windows;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    private ReviewService? _reviewService;
    private bool _isReviewDriverTurn;

    public ReviewPanelViewModel ReviewPanel { get; } = new();

    public bool ExtremeCodeEnabled => _settings.ExtremeCodeEnabled;

    public RelayCommand SendToReviewCommand { get; private set; } = null!;

    /// <summary>
    /// Initialize the Extreme Code review system. Called from constructor.
    /// </summary>
    private void InitializeReview()
    {
        SendToReviewCommand = new RelayCommand(StartCodeReview,
            () => ExtremeCodeEnabled && !IsProcessing && HasDialogHistory && !ReviewPanel.IsOpen);

        ReviewPanel.OnCloseRequested += () =>
        {
            _reviewService?.Stop();
            _reviewService = null;
            _isReviewDriverTurn = false;
        };

        ReviewPanel.OnJudgeVerdictSubmitted += verdict =>
        {
            _reviewService?.SendJudgeVerdict(verdict);
        };

        ReviewPanel.OnApplyFixesRequested += suggestions =>
        {
            ReviewPanel.IsOpen = false;
            _reviewService?.Stop();
            _reviewService = null;
            _isReviewDriverTurn = false;

            var prompt = $"""
                The following issues were identified during an Extreme Code Review.
                Please apply the agreed fixes:

                {suggestions}
                """;
            _ = SendDirectAsync(prompt, null);
        };
    }

    private void StartCodeReview()
    {
        if (_reviewService is not null)
        {
            _reviewService.Stop();
            _reviewService = null;
        }

        _isReviewDriverTurn = false;

        // Collect context
        var recentMessages = Messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant && !string.IsNullOrEmpty(m.Text))
            .Select(m => (role: m.Role == MessageRole.User ? "user" : "assistant", text: m.Text))
            .ToList();

        var gitDiff = _gitService.RunGit("diff HEAD", WorkingDirectory);
        var context = ReviewService.BuildReviewContext(ChangedFiles, recentMessages, gitDiff);

        // Create and configure review service
        _reviewService = new ReviewService();
        _reviewService.Configure(_cliService.ClaudeExePath, WorkingDirectory);

        // Wire events
        _reviewService.OnTextDelta += (role, text) =>
        {
            RunOnUI(() => ReviewPanel.AppendText(text));
        };

        _reviewService.OnMessageCompleted += (role, fullText) =>
        {
            RunOnUI(() =>
            {
                if (role == ReviewRole.System)
                {
                    ReviewPanel.AddMessage(role, fullText);
                }
                else if (role != ReviewRole.Driver)
                {
                    // Driver messages are completed by HandleCompleted via SubmitDriverResponse
                    ReviewPanel.CompleteMessage(fullText);
                }
            });
        };

        _reviewService.OnRoundStarted += round =>
        {
            RunOnUI(() =>
            {
                ReviewPanel.CurrentRound = round;
                if (round > 1)
                    ReviewPanel.AddMessage(ReviewRole.System, $"--- Round {round} ---");
            });
        };

        _reviewService.OnStatusChanged += status =>
        {
            RunOnUI(() =>
            {
                ReviewPanel.Status = status;
                if (status is ReviewStatus.Consensus)
                    ReviewPanel.IsReviewing = false;
            });
        };

        _reviewService.OnError += error =>
        {
            RunOnUI(() =>
            {
                ReviewPanel.AddMessage(ReviewRole.System, $"Error: {error}");
                ReviewPanel.IsReviewing = false;
            });
        };

        // Detect new streaming messages by role change (for Reviewer only).
        ReviewRole? lastStreamingRole = null;

        _reviewService.OnTextDelta += (role, text) =>
        {
            RunOnUI(() =>
            {
                if (lastStreamingRole != role)
                {
                    lastStreamingRole = role;
                    ReviewPanel.StartMessage(role);
                }
            });
        };

        // Wire driver response request — main Claude acts as Driver
        _reviewService.OnDriverResponseNeeded += prompt =>
        {
            RunOnUI(() =>
            {
                _isReviewDriverTurn = true;
                lastStreamingRole = ReviewRole.Driver;
                ReviewPanel.StartMessage(ReviewRole.Driver);

                // Send the reviewer's feedback to the main Claude
                _ = SendDirectAsync(prompt, null);
            });
        };

        // Reset panel and open
        ReviewPanel.Reset();
        ReviewPanel.MaxRounds = _settings.ReviewMaxRounds;
        ReviewPanel.IsOpen = true;

        // Start the review
        _reviewService.StartReview(context, _settings.ReviewMaxRounds);
    }
}
