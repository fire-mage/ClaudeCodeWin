using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Manages CLI sessions for planning features.
/// Each feature gets its own CLI session with a planner system prompt.
/// </summary>
public class PlannerService
{
    private readonly ConcurrentDictionary<string, PlannerSession> _sessions = new();
    private string? _claudeExePath;
    private string? _workingDirectory;

    public event Action<string, string, string?, List<BacklogPhase>>? OnPlanReady; // featureId, title, sessionId, phases
    public event Action<string, string>? OnQuestionAsked; // featureId, question
    public event Action<string, string>? OnPlannerError; // featureId, error
    public event Action<string, string>? OnPlannerTextDelta; // featureId, text

    public void Configure(string claudeExePath, string? workingDirectory)
    {
        _claudeExePath = claudeExePath;
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Start a planning session for a feature.
    /// </summary>
    public void StartPlanning(BacklogFeature feature)
    {
        if (_sessions.ContainsKey(feature.Id)) return;

        var cli = CreateCliService(feature.ProjectPath);

        // Resume existing session if available
        if (!string.IsNullOrEmpty(feature.PlannerSessionId))
            cli.RestoreSession(feature.PlannerSessionId);

        var session = new PlannerSession
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
        var prompt = $"Plan this feature:\n\n{feature.RawIdea}";
        if (!string.IsNullOrEmpty(feature.UserContext))
            prompt += $"\n\nAdditional context from user:\n{feature.UserContext}";
        cli.SendMessage(prompt);
    }

    /// <summary>
    /// Send the user's answer to a planner's question (via control_response).
    /// If the session crashed, starts a new planning session with the answer appended.
    /// Returns false if session was not found (caller should handle restart).
    /// </summary>
    public bool ResumePlanning(BacklogFeature feature, string userAnswer)
    {
        if (!_sessions.TryGetValue(feature.Id, out var session))
            return false; // Session not found — caller decides how to handle

        if (string.IsNullOrEmpty(session.PendingRequestId)) return false;

        var requestId = session.PendingRequestId;
        var toolUseId = session.PendingToolUseId;
        session.PendingRequestId = null;
        session.PendingToolUseId = null;

        // Build updatedInput with the answer
        var answersDict = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(session.PendingQuestionText))
            answersDict[session.PendingQuestionText] = userAnswer;

        var questionsJson = session.PendingQuestionsJson ?? "[]";
        var answersJson = JsonSerializer.Serialize(answersDict);
        var updatedInputJson = "{\"questions\":" + questionsJson + ",\"answers\":" + answersJson + "}";

        session.PendingQuestionText = null;
        session.PendingQuestionsJson = null;

        session.Cli.SendControlResponse(requestId, "allow",
            updatedInputJson: updatedInputJson, toolUseId: toolUseId);
        return true;
    }

    /// <summary>
    /// Stop planning for a specific feature.
    /// </summary>
    public void StopPlanning(string featureId)
    {
        if (!_sessions.TryRemove(featureId, out var session)) return;
        session.Cli.StopSession();
    }

    /// <summary>
    /// Stop all active planning sessions.
    /// </summary>
    public void StopAll()
    {
        // Atomically drain sessions one by one via TryRemove
        while (!_sessions.IsEmpty)
        {
            foreach (var key in _sessions.Keys)
            {
                if (_sessions.TryRemove(key, out var session))
                    session.Cli.StopSession();
            }
        }
    }

    public bool IsPlanning(string featureId) => _sessions.ContainsKey(featureId);

    public string? GetSessionId(string featureId)
    {
        return _sessions.TryGetValue(featureId, out var session) ? session.SessionId : null;
    }

    private ClaudeCliService CreateCliService(string? projectPath)
    {
        var cli = new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = projectPath ?? _workingDirectory,
            SystemPrompt = TeamPrompts.PlannerSystemPrompt,
            DangerouslySkipPermissions = false,
            ModelOverride = TeamPrompts.TeamModelId
        };
        return cli;
    }

    private void WireEvents(PlannerSession session)
    {
        var cli = session.Cli;
        var featureId = session.FeatureId;

        cli.OnTextDelta += text =>
        {
            session.Response.Append(text);
            OnPlannerTextDelta?.Invoke(featureId, text);
        };

        cli.OnSessionStarted += (sessionId, _, _) =>
        {
            session.SessionId = sessionId;
        };

        cli.OnCompleted += result =>
        {
            // Capture session ID for resume
            if (result.SessionId is not null)
                session.SessionId = result.SessionId;

            var fullText = session.Response.ToString();
            var (title, phases) = ParsePlan(fullText);
            var savedSessionId = session.SessionId;

            // Only StopSession if we won the TryRemove race — prevents double-stop
            // when StopPlanning() is called concurrently
            if (_sessions.TryRemove(featureId, out _))
                session.Cli.StopSession();

            if (phases.Count > 0)
                OnPlanReady?.Invoke(featureId, title, savedSessionId, phases);
            else
                OnPlannerError?.Invoke(featureId, "Planner did not produce a valid plan");
        };

        cli.OnError += error =>
        {
            if (_sessions.TryRemove(featureId, out _))
                session.Cli.StopSession();
            OnPlannerError?.Invoke(featureId, error);
        };

        // Handle permission/tool requests — planner is read-only
        cli.OnControlRequest += (requestId, toolName, toolUseId, input) =>
        {
            if (toolName == "AskUserQuestion")
            {
                HandleAskUserQuestion(session, requestId, toolUseId, input);
            }
            else
            {
                // Allow read-only tools only, deny everything else
                var allowed = toolName is "Read" or "Glob" or "Grep" or "WebFetch"
                    or "WebSearch";
                cli.SendControlResponse(requestId, allowed ? "allow" : "deny",
                    toolUseId: toolUseId);
            }
        };
    }

    private void HandleAskUserQuestion(PlannerSession session, string requestId,
        string toolUseId, JsonElement input)
    {
        session.PendingRequestId = requestId;
        session.PendingToolUseId = toolUseId;

        // Extract question text from the AskUserQuestion input
        var questionText = ExtractQuestionText(input);
        session.PendingQuestionText = questionText;

        // Store original questions JSON for building the response later
        try
        {
            if (input.TryGetProperty("questions", out var qa))
                session.PendingQuestionsJson = qa.GetRawText();
        }
        catch { }

        OnQuestionAsked?.Invoke(session.FeatureId, questionText);
    }

    private static string ExtractQuestionText(JsonElement input)
    {
        try
        {
            if (input.TryGetProperty("questions", out var questionsArr)
                && questionsArr.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var q in questionsArr.EnumerateArray())
                {
                    if (q.TryGetProperty("question", out var qText))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(qText.GetString() ?? "");

                        // Include options if present
                        if (q.TryGetProperty("options", out var opts)
                            && opts.ValueKind == JsonValueKind.Array)
                        {
                            var i = 1;
                            foreach (var opt in opts.EnumerateArray())
                            {
                                var label = opt.TryGetProperty("label", out var l)
                                    ? l.GetString() ?? "" : "";
                                var desc = opt.TryGetProperty("description", out var d)
                                    ? d.GetString() ?? "" : "";
                                sb.AppendLine();
                                sb.Append($"  {i}. {label}");
                                if (!string.IsNullOrEmpty(desc))
                                    sb.Append($" — {desc}");
                                i++;
                            }
                        }
                    }
                }
                return sb.ToString();
            }
        }
        catch { }

        return "Planner needs your input";
    }

    /// <summary>
    /// Parse the planner's response to extract the JSON plan.
    /// Looks for a JSON object with "title" and "phases" keys.
    /// </summary>
    internal static (string title, List<BacklogPhase> phases) ParsePlan(string text)
    {
        // Try to find JSON block in the text (may be wrapped in ```json ... ```)
        var jsonText = JsonBlockExtractor.Extract(text, "phases");
        if (jsonText is null)
            return ("", []);

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var phases = new List<BacklogPhase>();

            if (root.TryGetProperty("phases", out var phasesArr)
                && phasesArr.ValueKind == JsonValueKind.Array)
            {
                var order = 1;
                foreach (var p in phasesArr.EnumerateArray())
                {
                    var phase = new BacklogPhase
                    {
                        Order = order++,
                        Title = p.TryGetProperty("title", out var pt)
                            ? pt.GetString() ?? $"Phase {order - 1}" : $"Phase {order - 1}",
                        Plan = p.TryGetProperty("plan", out var pp)
                            ? pp.GetString() ?? "" : "",
                        AcceptanceCriteria = p.TryGetProperty("acceptanceCriteria", out var ac)
                            ? ac.GetString() : null
                    };
                    phases.Add(phase);
                }
            }

            return (title, phases);
        }
        catch (JsonException)
        {
            return ("", []);
        }
    }


    private class PlannerSession
    {
        public required ClaudeCliService Cli;
        public required string FeatureId;
        public StringBuilder Response = new();
        public string? SessionId;
        public string? PendingRequestId;
        public string? PendingToolUseId;
        public string? PendingQuestionText;
        public string? PendingQuestionsJson;
    }
}
