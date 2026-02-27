using System.Text;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Manages the Extreme Code review debate between a Reviewer CLI and the main Claude (Driver).
/// The Reviewer runs as a separate CLI process; the Driver role is handled by the main assistant
/// in the chat, which has full conversation context.
/// </summary>
public class ReviewService
{
    private ClaudeCliService? _reviewerCli;
    private readonly StringBuilder _currentResponse = new();
    private bool _isActive;
    private int _currentRound;
    private int _maxRounds;
    private string? _claudeExePath;
    private string? _workingDirectory;
    private Action<string>? _pendingDriverCallback;

    // Events for UI updates
    public event Action<ReviewRole, string>? OnTextDelta; // role, text chunk
    public event Action<ReviewRole, string>? OnMessageCompleted; // role, full text
    public event Action<ReviewStatus>? OnStatusChanged;
    public event Action<int>? OnRoundStarted; // round number
    public event Action<string>? OnError;

    /// <summary>
    /// Fired when it's the Driver's turn. The main Claude should respond to this prompt.
    /// Call <see cref="SubmitDriverResponse"/> when the response is ready.
    /// </summary>
    public event Action<string>? OnDriverResponseNeeded; // reviewer feedback for main Claude

    public bool IsActive => _isActive;
    public int CurrentRound => _currentRound;

    public void Configure(string claudeExePath, string? workingDirectory)
    {
        _claudeExePath = claudeExePath;
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Start a review session. Sends the context to the Reviewer CLI.
    /// </summary>
    public void StartReview(string context, int maxRounds)
    {
        if (_isActive) return;

        _isActive = true;
        _maxRounds = maxRounds;
        _currentRound = 1;

        // Create reviewer CLI
        _reviewerCli = CreateCliService();

        OnStatusChanged?.Invoke(ReviewStatus.InProgress);
        OnRoundStarted?.Invoke(_currentRound);

        var prompt = BuildReviewerPrompt(context);
        SendToReviewer(prompt, reviewerResponseText =>
        {
            // Reviewer finished — now send to Driver (main Claude)
            if (!_isActive) return;

            var verdict = DetectVerdict(reviewerResponseText);
            if (verdict == ReviewVerdict.Consensus)
            {
                OnStatusChanged?.Invoke(ReviewStatus.Consensus);
                return;
            }

            // Request driver response from the main Claude
            var driverPrompt = BuildDriverPrompt(reviewerResponseText);
            RequestDriverResponse(driverPrompt, HandleDriverResponse);
        });
    }

    /// <summary>
    /// Inject a Judge (user) verdict into the debate.
    /// </summary>
    public void SendJudgeVerdict(string verdict)
    {
        if (!_isActive) return;

        OnMessageCompleted?.Invoke(ReviewRole.Judge, verdict);

        // Send verdict to reviewer for acknowledgment
        if (_reviewerCli is not null)
        {
            var prompt = $"The Judge (user) has made a decision:\n\n{verdict}\n\nAcknowledge the decision briefly.";
            SendToReviewer(prompt, _ =>
            {
                OnStatusChanged?.Invoke(ReviewStatus.Consensus);
            });
        }
        else
        {
            OnStatusChanged?.Invoke(ReviewStatus.Consensus);
        }
    }

    /// <summary>
    /// Called by MainViewModel when the main Claude has finished responding as Driver.
    /// </summary>
    public void SubmitDriverResponse(string responseText)
    {
        if (_pendingDriverCallback is null) return;

        OnMessageCompleted?.Invoke(ReviewRole.Driver, responseText);

        var callback = _pendingDriverCallback;
        _pendingDriverCallback = null;
        callback(responseText);
    }

    public void Stop()
    {
        _isActive = false;
        _pendingDriverCallback = null;
        _reviewerCli?.StopSession();
        _reviewerCli = null;
    }

    private void HandleDriverResponse(string driverResponseText)
    {
        if (!_isActive) return;

        var verdict = DetectVerdict(driverResponseText);

        if (verdict == ReviewVerdict.Agree || verdict == ReviewVerdict.Consensus)
        {
            OnStatusChanged?.Invoke(ReviewStatus.Consensus);
            return;
        }

        if (verdict == ReviewVerdict.Escalate)
        {
            OnStatusChanged?.Invoke(ReviewStatus.Escalated);
            return;
        }

        _currentRound++;

        if (_currentRound > _maxRounds)
        {
            // Max rounds reached — escalate to judge
            OnMessageCompleted?.Invoke(ReviewRole.System,
                $"Max rounds ({_maxRounds}) reached without consensus. Escalating to Judge.");
            OnStatusChanged?.Invoke(ReviewStatus.Escalated);
            return;
        }

        OnRoundStarted?.Invoke(_currentRound);

        // Continue debate: send driver's response back to reviewer
        var reviewerFollowup = BuildReviewerFollowupPrompt(driverResponseText);
        SendToReviewer(reviewerFollowup, reviewerText =>
        {
            if (!_isActive) return;

            var reviewerVerdict = DetectVerdict(reviewerText);
            if (reviewerVerdict == ReviewVerdict.Consensus || reviewerVerdict == ReviewVerdict.Agree)
            {
                OnStatusChanged?.Invoke(ReviewStatus.Consensus);
                return;
            }

            if (reviewerVerdict == ReviewVerdict.Escalate)
            {
                OnStatusChanged?.Invoke(ReviewStatus.Escalated);
                return;
            }

            // Driver responds again (via main Claude)
            var driverFollowup = BuildDriverFollowupPrompt(reviewerText);
            RequestDriverResponse(driverFollowup, HandleDriverResponse);
        });
    }

    /// <summary>
    /// Request a response from the main Claude acting as Driver.
    /// Saves the continuation callback and fires the event.
    /// </summary>
    private void RequestDriverResponse(string prompt, Action<string> onCompleted)
    {
        _pendingDriverCallback = onCompleted;
        OnDriverResponseNeeded?.Invoke(prompt);
    }

    private ClaudeCliService CreateCliService()
    {
        var cli = new ClaudeCliService();
        cli.ClaudeExePath = _claudeExePath ?? "claude";
        cli.WorkingDirectory = _workingDirectory;
        return cli;
    }

    /// <summary>
    /// Send a prompt to the Reviewer CLI and wire one-shot event handlers.
    /// </summary>
    private void SendToReviewer(string prompt, Action<string> onCompleted)
    {
        var cli = _reviewerCli;
        if (cli is null) return;

        var responseBuilder = new StringBuilder();

        // Wire events (one-shot handlers)
        Action<string>? textHandler = null;
        Action<ResultData>? completedHandler = null;
        Action<string>? errorHandler = null;

        textHandler = text =>
        {
            responseBuilder.Append(text);
            OnTextDelta?.Invoke(ReviewRole.Reviewer, text);
        };

        completedHandler = result =>
        {
            // Unsubscribe
            cli.OnTextDelta -= textHandler;
            cli.OnCompleted -= completedHandler;
            cli.OnError -= errorHandler;

            var fullText = responseBuilder.ToString();
            OnMessageCompleted?.Invoke(ReviewRole.Reviewer, fullText);
            onCompleted(fullText);
        };

        errorHandler = error =>
        {
            cli.OnTextDelta -= textHandler;
            cli.OnCompleted -= completedHandler;
            cli.OnError -= errorHandler;

            OnError?.Invoke($"Reviewer: {error}");
            if (_isActive)
                OnStatusChanged?.Invoke(ReviewStatus.Dismissed);
        };

        cli.OnTextDelta += textHandler;
        cli.OnCompleted += completedHandler;
        cli.OnError += errorHandler;

        // Auto-confirm any permission requests (reviewer is read-only conceptually)
        cli.OnControlRequest += (requestId, toolName, toolUseId, input) =>
        {
            cli.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
        };

        cli.SendMessage(prompt);
    }

    private static string BuildReviewerPrompt(string context)
    {
        return $"""
            You are a senior code reviewer performing an Extreme Code Review.
            Another developer (the "Driver") has written the code below. Your job is to review it critically but constructively.

            Focus on:
            1. **Bugs** — logic errors, off-by-one, null references, race conditions
            2. **Security** — injection, XSS, path traversal, credential exposure
            3. **Edge cases** — missing error handling, boundary conditions
            4. **Convention violations** — inconsistency with project patterns
            5. **Architecture** — only if there's a clear structural problem

            Do NOT comment on:
            - Code style preferences (formatting, naming conventions unless truly confusing)
            - Minor optimizations without measurable impact
            - "Nice to have" suggestions

            For each issue found, provide:
            - **Severity**: CRITICAL / WARNING / SUGGESTION
            - **Location**: file and approximate area
            - **Problem**: what's wrong
            - **Fix**: how to fix it

            At the end of your review, state exactly one of these verdicts:
            - `VERDICT: CONSENSUS` — code is good, only minor suggestions
            - `VERDICT: ISSUES_FOUND` — there are problems that need fixing

            Here is the code to review:

            {context}
            """;
    }

    /// <summary>
    /// Driver prompt for the main Claude — simplified, no context repetition
    /// (the main Claude already has full conversation context).
    /// </summary>
    private static string BuildDriverPrompt(string reviewerFeedback)
    {
        return $"""
            An independent code reviewer has analyzed your recent changes and provided the following feedback.
            Please evaluate each point honestly:
            - If a point is valid: acknowledge it and explain how you'd fix it
            - If a point is invalid: explain specifically why your approach is correct
            - If partially valid: explain the nuance

            Reviewer's feedback:

            {reviewerFeedback}

            At the end, state exactly one of these verdicts:
            - `VERDICT: AGREE` — you accept all the reviewer's points
            - `VERDICT: PARTIALLY_AGREE` — some points are valid, others are not
            - `VERDICT: DISAGREE` — explain why the reviewer is wrong
            - `VERDICT: ESCALATE` — you want the user (Judge) to decide
            """;
    }

    private static string BuildReviewerFollowupPrompt(string driverResponse)
    {
        return $"""
            The Driver has responded to your review:

            {driverResponse}

            Evaluate the Driver's response:
            - If their counter-arguments are valid, update your position
            - If you still believe there are issues, explain why with specific evidence

            State your verdict:
            - `VERDICT: CONSENSUS` — you agree the code is fine (or your concerns were addressed)
            - `VERDICT: ISSUES_FOUND` — you still believe there are unresolved issues
            - `VERDICT: ESCALATE` — you want the user (Judge) to decide
            """;
    }

    private static string BuildDriverFollowupPrompt(string reviewerResponse)
    {
        return $"""
            The Reviewer has responded with additional feedback:

            {reviewerResponse}

            Continue the discussion. Evaluate their points:
            - If valid: acknowledge
            - If invalid: explain why

            State your verdict:
            - `VERDICT: AGREE` — you accept the reviewer's points
            - `VERDICT: PARTIALLY_AGREE` — some valid, some not
            - `VERDICT: DISAGREE` — explain why
            - `VERDICT: ESCALATE` — you want the user (Judge) to decide
            """;
    }

    private enum ReviewVerdict { None, Consensus, Agree, PartiallyAgree, Disagree, Escalate }

    private static ReviewVerdict DetectVerdict(string text)
    {
        // Look for verdict marker in the last 500 chars
        var tail = text.Length > 500 ? text[^500..] : text;
        var upper = tail.ToUpperInvariant();

        if (upper.Contains("VERDICT: CONSENSUS") || upper.Contains("VERDICT:CONSENSUS"))
            return ReviewVerdict.Consensus;
        if (upper.Contains("VERDICT: AGREE") || upper.Contains("VERDICT:AGREE"))
            return ReviewVerdict.Agree;
        if (upper.Contains("VERDICT: ESCALATE") || upper.Contains("VERDICT:ESCALATE"))
            return ReviewVerdict.Escalate;
        if (upper.Contains("VERDICT: DISAGREE") || upper.Contains("VERDICT:DISAGREE"))
            return ReviewVerdict.Disagree;
        if (upper.Contains("VERDICT: PARTIALLY_AGREE") || upper.Contains("VERDICT:PARTIALLY_AGREE"))
            return ReviewVerdict.PartiallyAgree;

        return ReviewVerdict.None;
    }

    /// <summary>
    /// Collects review context from the current tab's state.
    /// </summary>
    public static string BuildReviewContext(
        IEnumerable<string> changedFiles,
        IEnumerable<(string role, string text)> recentMessages,
        string? gitDiff)
    {
        var sb = new StringBuilder();

        // Task description (first user message)
        sb.AppendLine("## Task");
        var firstUserMsg = recentMessages.FirstOrDefault(m => m.role == "user");
        if (firstUserMsg != default)
            sb.AppendLine(firstUserMsg.text);

        sb.AppendLine();

        // Assistant's work (last few assistant messages)
        sb.AppendLine("## Assistant's Work");
        foreach (var msg in recentMessages.Where(m => m.role == "assistant").TakeLast(3))
        {
            sb.AppendLine(msg.text);
            sb.AppendLine("---");
        }

        sb.AppendLine();

        // Changed files
        if (changedFiles.Any())
        {
            sb.AppendLine("## Changed Files");
            foreach (var f in changedFiles)
                sb.AppendLine($"- {f}");
            sb.AppendLine();
        }

        // Git diff
        if (!string.IsNullOrEmpty(gitDiff))
        {
            sb.AppendLine("## Git Diff");
            // Truncate very large diffs
            if (gitDiff.Length > 10000)
            {
                sb.AppendLine(gitDiff[..10000]);
                sb.AppendLine($"\n... (truncated, {gitDiff.Length} chars total)");
            }
            else
            {
                sb.AppendLine(gitDiff);
            }
        }

        return sb.ToString();
    }
}
