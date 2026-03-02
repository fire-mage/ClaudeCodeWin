using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Manages CLI sessions for reviewing implementation plans.
/// Each feature gets its own CLI session with a plan reviewer system prompt.
/// Reviews plan completeness, feasibility, and missed requirements.
/// </summary>
public class PlanReviewerService
{
    private readonly ConcurrentDictionary<string, ReviewerSession> _sessions = new();
    private string? _claudeExePath;
    private string? _workingDirectory;

    public event Action<string, PlanReviewResult>? OnReviewComplete; // featureId, result
    public event Action<string, string>? OnReviewError; // featureId, error
    public event Action<string, string>? OnReviewTextDelta; // featureId, text

    public void Configure(string claudeExePath, string? workingDirectory)
    {
        _claudeExePath = claudeExePath;
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Start a plan review session for a feature.
    /// </summary>
    public void StartReview(BacklogFeature feature)
    {
        if (_sessions.ContainsKey(feature.Id)) return;

        var cli = new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = feature.ProjectPath ?? _workingDirectory,
            SystemPrompt = TeamPrompts.BuildPlanReviewerSystemPrompt(),
            DangerouslySkipPermissions = false,
            ModelOverride = TeamPrompts.TeamModelId
        };

        var session = new ReviewerSession
        {
            Cli = cli,
            FeatureId = feature.Id
        };

        if (!_sessions.TryAdd(feature.Id, session))
        {
            cli.StopSession();
            return;
        }

        WireEvents(session);

        // Build the review prompt with full context
        var prompt = BuildReviewPrompt(feature);
        cli.SendMessage(prompt);
    }

    /// <summary>
    /// Stop review for a specific feature.
    /// </summary>
    public void StopReview(string featureId)
    {
        if (!_sessions.TryRemove(featureId, out var session)) return;
        session.Cli.StopSession();
    }

    /// <summary>
    /// Stop all active review sessions.
    /// </summary>
    public void StopAll()
    {
        while (!_sessions.IsEmpty)
        {
            foreach (var key in _sessions.Keys)
            {
                if (_sessions.TryRemove(key, out var session))
                    session.Cli.StopSession();
            }
        }
    }

    public bool IsReviewing(string featureId) => _sessions.ContainsKey(featureId);

    private static string BuildReviewPrompt(BacklogFeature feature)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Review this implementation plan:");
        sb.AppendLine();
        sb.AppendLine("## Original Idea");
        sb.AppendLine(feature.RawIdea);

        if (!string.IsNullOrEmpty(feature.UserContext))
        {
            sb.AppendLine();
            sb.AppendLine("## Additional Context");
            sb.AppendLine(feature.UserContext);
        }

        if (!string.IsNullOrEmpty(feature.AnalysisResult))
        {
            sb.AppendLine();
            sb.AppendLine("## Analysis Summary");
            sb.AppendLine(feature.AnalysisResult);
        }

        sb.AppendLine();
        sb.AppendLine($"## Plan Title: {feature.Title ?? "(untitled)"}");
        sb.AppendLine();
        sb.AppendLine("## Phases");

        foreach (var phase in feature.Phases.OrderBy(p => p.Order))
        {
            sb.AppendLine($"### Phase {phase.Order}: {phase.Title}");
            sb.AppendLine(phase.Plan);
            if (!string.IsNullOrEmpty(phase.AcceptanceCriteria))
                sb.AppendLine($"Acceptance Criteria: {phase.AcceptanceCriteria}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private void WireEvents(ReviewerSession session)
    {
        var cli = session.Cli;
        var featureId = session.FeatureId;

        cli.OnTextDelta += text =>
        {
            session.Response.Append(text);
            OnReviewTextDelta?.Invoke(featureId, text);
        };

        cli.OnCompleted += result =>
        {
            var fullText = session.Response.ToString();
            var reviewResult = ParseReview(fullText);

            if (_sessions.TryRemove(featureId, out _))
            {
                session.Cli.StopSession();
                OnReviewComplete?.Invoke(featureId, reviewResult);
            }
        };

        cli.OnError += error =>
        {
            if (_sessions.TryRemove(featureId, out _))
            {
                session.Cli.StopSession();
                OnReviewError?.Invoke(featureId, error);
            }
        };

        // Handle permission/tool requests — reviewer is read-only
        cli.OnControlRequest += (requestId, toolName, toolUseId, input) =>
        {
            // Allow read-only tools, deny everything else
            var allowed = toolName is "Read" or "Glob" or "Grep" or "WebFetch"
                or "WebSearch";
            cli.SendControlResponse(requestId, allowed ? "allow" : "deny",
                toolUseId: toolUseId);
        };
    }

    /// <summary>
    /// Parse the reviewer's response to extract the JSON review result.
    /// </summary>
    internal static PlanReviewResult ParseReview(string text)
    {
        var jsonText = JsonBlockExtractor.Extract(text);
        if (jsonText is null)
            return new PlanReviewResult
            {
                Verdict = "error",
                Comments = "Could not parse review output: " + text
            };

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            var result = new PlanReviewResult
            {
                Verdict = root.TryGetProperty("verdict", out var v)
                    ? v.GetString() ?? "error" : "error",
                Comments = root.TryGetProperty("comments", out var c)
                    ? c.GetString() ?? "" : ""
            };

            if (root.TryGetProperty("suggestions", out var suggestions)
                && suggestions.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in suggestions.EnumerateArray())
                {
                    var text2 = s.GetString();
                    if (!string.IsNullOrEmpty(text2))
                        result.Suggestions.Add(text2);
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return new PlanReviewResult
            {
                Verdict = "error",
                Comments = "JSON parse error in review output"
            };
        }
    }

    private class ReviewerSession
    {
        public required ClaudeCliService Cli;
        public required string FeatureId;
        public StringBuilder Response = new();
    }
}

/// <summary>
/// Result of a plan review by the PlanReviewerService.
/// </summary>
public class PlanReviewResult
{
    public string Verdict { get; set; } = "error";
    public string Comments { get; set; } = "";
    public List<string> Suggestions { get; set; } = [];
}
