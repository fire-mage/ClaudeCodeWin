using System.Text;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Runs a single-pass code review using a separate Claude CLI process.
/// The reviewer analyses the code context and returns a verdict (Consensus or IssuesFound).
/// </summary>
public class ReviewService
{
    private ClaudeCliService? _reviewerCli;
    private readonly StringBuilder _responseBuilder = new();
    private bool _isActive;
    private string? _claudeExePath;
    private string? _workingDirectory;

    public event Action<string>? OnTextDelta;
    public event Action<string, ReviewVerdict>? OnReviewCompleted;
    public event Action<string>? OnError;
    public event Action<int>? OnStreamStalled;
    public event Action? OnStreamResumed;

    public bool IsActive => _isActive;

    public void Configure(string claudeExePath, string? workingDirectory)
    {
        _claudeExePath = claudeExePath;
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Start a single-pass review. Sends context to the Reviewer CLI and streams the response.
    /// </summary>
    public void RunReview(string context)
    {
        if (_isActive) return;

        _isActive = true;
        _responseBuilder.Clear();

        _reviewerCli = CreateCliService();

        var prompt = BuildReviewerPrompt(context);
        SendToReviewer(prompt);
    }

    /// <summary>
    /// Send a nudge message to the reviewer CLI via stdin to unstick it if it's hung.
    /// </summary>
    public void SendNudge()
    {
        if (_isActive && _reviewerCli is not null)
        {
            _reviewerCli.SendMessage("Are you still working? Please continue with your review and provide a verdict.");
        }
    }

    public void Stop()
    {
        _isActive = false;
        _reviewerCli?.StopSession();
        _reviewerCli = null;
    }

    private ClaudeCliService CreateCliService()
    {
        var cli = new ClaudeCliService();
        cli.ClaudeExePath = _claudeExePath ?? "claude";
        cli.WorkingDirectory = _workingDirectory;
        return cli;
    }

    private void SendToReviewer(string prompt)
    {
        var cli = _reviewerCli;
        if (cli is null) return;

        Action<string>? textHandler = null;
        Action<ResultData>? completedHandler = null;
        Action<string>? errorHandler = null;
        Action<int>? stallHandler = null;
        Action? resumeHandler = null;
        Action<string, string, string, System.Text.Json.JsonElement>? controlHandler = null;

        void UnsubscribeAll()
        {
            cli.OnTextDelta -= textHandler;
            cli.OnCompleted -= completedHandler;
            cli.OnError -= errorHandler;
            cli.OnStreamStalled -= stallHandler;
            cli.OnStreamResumed -= resumeHandler;
            cli.OnControlRequest -= controlHandler;
        }

        textHandler = text =>
        {
            _responseBuilder.Append(text);
            OnTextDelta?.Invoke(text);
        };

        completedHandler = result =>
        {
            UnsubscribeAll();
            var fullText = _responseBuilder.ToString();
            var verdict = DetectVerdict(fullText);
            _isActive = false;
            OnReviewCompleted?.Invoke(fullText, verdict);
        };

        errorHandler = error =>
        {
            UnsubscribeAll();
            _isActive = false;
            OnError?.Invoke($"Reviewer: {error}");
        };

        stallHandler = seconds => OnStreamStalled?.Invoke(seconds);
        resumeHandler = () => OnStreamResumed?.Invoke();

        cli.OnTextDelta += textHandler;
        cli.OnCompleted += completedHandler;
        cli.OnError += errorHandler;
        cli.OnStreamStalled += stallHandler;
        cli.OnStreamResumed += resumeHandler;

        // Only allow read-only tools; deny anything that could modify files or run commands
        controlHandler = (requestId, toolName, toolUseId, input) =>
        {
            var allowed = toolName is "Read" or "Glob" or "Grep" or "WebFetch" or "WebSearch";
            cli.SendControlResponse(requestId, allowed ? "allow" : "deny", toolUseId: toolUseId);
        };
        cli.OnControlRequest += controlHandler;

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

    internal static ReviewVerdict DetectVerdict(string text)
    {
        // Look for verdict marker in the last 500 chars
        var tail = text.Length > 500 ? text[^500..] : text;
        var upper = tail.ToUpperInvariant();

        if (upper.Contains("VERDICT: CONSENSUS") || upper.Contains("VERDICT:CONSENSUS"))
            return ReviewVerdict.Consensus;
        if (upper.Contains("VERDICT: ISSUES_FOUND") || upper.Contains("VERDICT:ISSUES_FOUND"))
            return ReviewVerdict.IssuesFound;

        // Legacy compat: AGREE also means consensus
        if (upper.Contains("VERDICT: AGREE") || upper.Contains("VERDICT:AGREE"))
            return ReviewVerdict.Consensus;

        return ReviewVerdict.None;
    }

    /// <summary>
    /// Collects review context from the current session state.
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
