using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    private ReviewService? _reviewService;
    private int _reviewAttempt;
    private bool _isAutoReviewPending;
    private MessageViewModel? _currentReviewerMessage;
    private string? _lastReviewCriticalSnippet;
    private int _currentTaskStartIndex;

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
            DiagnosticLogger.Log("AUTO_REVIEW_CHECK", "re-review path: _isAutoReviewPending was true");
            TryStartAutoReview();
            return;
        }

        // Auto-trigger first review after task completion (only if project files changed)
        var reviewEnabled = _settings.ReviewerEnabled;
        var hasMarker = DetectCompletionMarker();
        var hasProjectFiles = HasProjectFileChanges();

        DiagnosticLogger.Log("AUTO_REVIEW_CHECK",
            $"reviewEnabled={reviewEnabled} hasMarker={hasMarker} hasProjectFiles={hasProjectFiles} " +
            $"changedFiles={ChangedFiles.Count} wd={WorkingDirectory}");

        if (reviewEnabled && hasMarker && hasProjectFiles)
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
        // Stop any previous review CLI process before starting a new one
        // Preserve review attempt counter and loop detection state
        var savedAttempt = _reviewAttempt;
        var savedSnippet = _lastReviewCriticalSnippet;
        CancelReview();
        _reviewAttempt = savedAttempt;
        _lastReviewCriticalSnippet = savedSnippet;

        // Collect context from the current task only (from user's task message onward)
        // _currentTaskStartIndex is set in SendMessageAsync when user initiates a task,
        // so it excludes previous tasks, and clarifications don't reset it
        var recentMessages = Messages
            .Skip(Math.Min(_currentTaskStartIndex, Messages.Count))
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant
                        && !m.IsReviewerMessage
                        && !string.IsNullOrEmpty(m.Text))
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

        if (_reviewAttempt >= _settings.ReviewAutoRetries)
        {
            Messages.Add(new MessageViewModel(MessageRole.System,
                $"Review found issues after {_reviewAttempt} attempts. Awaiting your decision."));
            TryShowTaskSuggestion();
            return;
        }

        // Loop detection: if reviewer repeats the same CRITICAL finding, stop early
        var criticalSnippet = ExtractFirstCritical(reviewText);
        if (criticalSnippet is not null && criticalSnippet == _lastReviewCriticalSnippet)
        {
            Messages.Add(new MessageViewModel(MessageRole.System,
                "Review loop detected — same critical issue repeated. Stopping auto-review."));
            TryShowTaskSuggestion();
            return;
        }
        _lastReviewCriticalSnippet = criticalSnippet;

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
        _lastReviewCriticalSnippet = null;
        if (_currentReviewerMessage is not null)
        {
            _currentReviewerMessage.IsStreaming = false;
            _currentReviewerMessage = null;
        }
    }

    /// <summary>
    /// Returns true if any changed file is within the project's working directory
    /// (excludes memory files, KB articles, and other files outside the project).
    /// </summary>
    private bool HasProjectFileChanges()
    {
        if (string.IsNullOrEmpty(WorkingDirectory) || ChangedFiles.Count == 0)
        {
            DiagnosticLogger.Log("PROJECT_FILES", $"empty: wd={WorkingDirectory} count={ChangedFiles.Count}");
            return false;
        }

        var wd = WorkingDirectory.Replace('\\', '/').TrimEnd('/') + "/";
        var match = ChangedFiles.Any(f => f.Replace('\\', '/').StartsWith(wd, StringComparison.OrdinalIgnoreCase));
        if (!match)
        {
            DiagnosticLogger.Log("PROJECT_FILES",
                $"no match: wd={wd} files=[{string.Join(", ", ChangedFiles.Take(5))}]");
        }
        return match;
    }

    /// <summary>
    /// Extracts a normalized snippet of the first CRITICAL finding from review text.
    /// Returns null if no CRITICAL found.
    /// </summary>
    private static string? ExtractFirstCritical(string reviewText)
    {
        var lines = reviewText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("CRITICAL", StringComparison.OrdinalIgnoreCase))
            {
                // Take this line + next few lines as the "critical snippet" (normalized)
                var snippet = string.Join(" ", lines.Skip(i).Take(3))
                    .Trim().ToLowerInvariant();
                return snippet.Length > 200 ? snippet[..200] : snippet;
            }
        }
        return null;
    }
}
