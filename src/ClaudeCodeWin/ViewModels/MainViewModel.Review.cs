using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    private ReviewService? _reviewService;
    private int _reviewAttempt;
    private bool _isAutoReviewPending;
    private MessageViewModel? _currentReviewerMessage;

    public bool ReviewerEnabled => _settings.ReviewerEnabled;

    /// <summary>
    /// Called from HandleCompleted after every turn.
    /// Decides whether to auto-trigger a review or show FinalizeActions.
    /// </summary>
    private void OnTurnCompleted()
    {
        // If a review cycle just finished and fixes were applied, auto re-review
        if (_isAutoReviewPending)
        {
            _isAutoReviewPending = false;
            TryStartAutoReview();
            return;
        }

        // Auto-trigger first review after task completion
        if (_settings.ReviewerEnabled && DetectCompletionMarker() && ChangedFiles.Count > 0)
        {
            _reviewAttempt = 0;
            TryStartAutoReview();
            return;
        }

        TryShowTaskSuggestion();
    }

    private void TryStartAutoReview()
    {
        // Stop any previous review CLI process before starting a new one
        CancelReview();

        // Collect fresh context (new git diff for each re-review)
        var recentMessages = Messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant && !string.IsNullOrEmpty(m.Text))
            .Select(m => (role: m.Role == MessageRole.User ? "user" : "assistant", text: m.Text))
            .ToList();

        var gitDiff = _gitService.RunGit("diff HEAD", WorkingDirectory);
        var context = ReviewService.BuildReviewContext(ChangedFiles, recentMessages, gitDiff);

        _reviewService = new ReviewService();
        _reviewService.Configure(_cliService.ClaudeExePath, WorkingDirectory);

        // System message
        Messages.Add(new MessageViewModel(MessageRole.System,
            _reviewAttempt == 0
                ? "Starting code review..."
                : $"Re-reviewing code (attempt {_reviewAttempt + 1})..."));

        // Reviewer message bubble for streaming
        _currentReviewerMessage = new MessageViewModel(MessageRole.Assistant)
        {
            IsStreaming = true,
            ReviewerLabel = "Reviewer"
        };
        Messages.Add(_currentReviewerMessage);

        // Wire events
        _reviewService.OnTextDelta += text =>
        {
            RunOnUI(() =>
            {
                if (_currentReviewerMessage is not null)
                {
                    _currentReviewerMessage.IsThinking = false;
                    _currentReviewerMessage.Text += text;
                }
            });
        };

        _reviewService.OnReviewCompleted += (fullText, verdict) =>
        {
            RunOnUI(() =>
            {
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
                if (_currentReviewerMessage is not null)
                {
                    _currentReviewerMessage.IsStreaming = false;
                    _currentReviewerMessage.Text = $"Review error: {error}";
                    _currentReviewerMessage = null;
                }
                _reviewService = null;
                Messages.Add(new MessageViewModel(MessageRole.System, "Review failed. Proceeding without review."));
                TryShowTaskSuggestion();
            });
        };

        _reviewService.RunReview(context);
    }

    private void HandleReviewVerdict(ReviewVerdict verdict, string reviewText)
    {
        _reviewService?.Stop();
        _reviewService = null;

        if (verdict == ReviewVerdict.Consensus)
        {
            Messages.Add(new MessageViewModel(MessageRole.System, "Review passed — no issues found."));
            TryShowTaskSuggestion();
            return;
        }

        // ISSUES_FOUND (or None — treat as issues found to be safe)
        _reviewAttempt++;

        if (_reviewAttempt > _settings.ReviewAutoRetries)
        {
            Messages.Add(new MessageViewModel(MessageRole.System,
                $"Review found issues after {_reviewAttempt} attempts. Awaiting your decision."));
            TryShowTaskSuggestion();
            return;
        }

        // Auto-fix: send reviewer's feedback as prompt to main Claude
        _isAutoReviewPending = true;
        var fixPrompt = $"""
            A code reviewer found issues in your recent work:

            {reviewText}

            Please fix the issues identified above. After fixing, provide a brief summary of what you changed.
            """;
        SendDirectAsync(fixPrompt, null).ContinueWith(t =>
        {
            if (t.Exception is not null)
                DiagnosticLogger.Log("REVIEW_FIX_ERROR", t.Exception.InnerException?.Message ?? t.Exception.Message);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Cancel any active review (e.g. when user sends a message or cancels processing).
    /// </summary>
    private void CancelReview()
    {
        if (_reviewService is not null)
        {
            _reviewService.Stop();
            _reviewService = null;
        }
        _isAutoReviewPending = false;
        _reviewAttempt = 0;
        if (_currentReviewerMessage is not null)
        {
            _currentReviewerMessage.IsStreaming = false;
            _currentReviewerMessage = null;
        }
    }
}
