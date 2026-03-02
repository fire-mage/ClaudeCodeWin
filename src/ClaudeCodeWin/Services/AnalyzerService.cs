using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Manages CLI sessions for analyzing feature ideas.
/// Each feature gets its own CLI session with an analyzer system prompt.
/// Evaluates feasibility, identifies affected projects, flags risks.
/// </summary>
public class AnalyzerService
{
    private readonly ConcurrentDictionary<string, AnalyzerSession> _sessions = new();
    private string? _claudeExePath;
    private string? _workingDirectory;
    private string? _systemPrompt;

    public event Action<string, AnalysisResult>? OnAnalysisComplete; // featureId, result
    public event Action<string, string>? OnQuestionAsked; // featureId, question
    public event Action<string, string>? OnAnalysisError; // featureId, error
    public event Action<string, string>? OnAnalysisTextDelta; // featureId, text

    public void Configure(string claudeExePath, string? workingDirectory,
        string systemPrompt)
    {
        _claudeExePath = claudeExePath;
        _workingDirectory = workingDirectory;
        _systemPrompt = systemPrompt;
    }

    public void UpdateSystemPrompt(string systemPrompt)
    {
        _systemPrompt = systemPrompt;
    }

    /// <summary>
    /// Start an analysis session for a feature.
    /// </summary>
    public void StartAnalysis(BacklogFeature feature)
    {
        if (_sessions.ContainsKey(feature.Id)) return;

        var cli = CreateCliService(feature.ProjectPath);

        // Resume existing session if available
        if (!string.IsNullOrEmpty(feature.AnalysisSessionId))
            cli.RestoreSession(feature.AnalysisSessionId);

        var session = new AnalyzerSession
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
        var prompt = $"Analyze this feature idea:\n\n{feature.RawIdea}";
        if (!string.IsNullOrEmpty(feature.UserContext))
            prompt += $"\n\nAdditional context from user:\n{feature.UserContext}";
        cli.SendMessage(prompt);
    }

    /// <summary>
    /// Send the user's answer to an analyzer's question (via control_response).
    /// Returns false if session was not found.
    /// </summary>
    public bool ResumeAnalysis(BacklogFeature feature, string userAnswer)
    {
        if (!_sessions.TryGetValue(feature.Id, out var session))
            return false;

        if (string.IsNullOrEmpty(session.PendingRequestId)) return false;

        var requestId = session.PendingRequestId;
        var toolUseId = session.PendingToolUseId;
        session.PendingRequestId = null;
        session.PendingToolUseId = null;

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
    /// Stop analysis for a specific feature.
    /// </summary>
    public void StopAnalysis(string featureId)
    {
        if (!_sessions.TryRemove(featureId, out var session)) return;
        session.Cli.StopSession();
    }

    /// <summary>
    /// Stop all active analysis sessions.
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

    public bool IsAnalyzing(string featureId) => _sessions.ContainsKey(featureId);

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
            SystemPrompt = _systemPrompt ?? "",
            DangerouslySkipPermissions = false,
            ModelOverride = TeamPrompts.TeamModelId
        };
        return cli;
    }

    private void WireEvents(AnalyzerSession session)
    {
        var cli = session.Cli;
        var featureId = session.FeatureId;

        cli.OnTextDelta += text =>
        {
            session.Response.Append(text);
            OnAnalysisTextDelta?.Invoke(featureId, text);
        };

        cli.OnSessionStarted += (sessionId, _, _) =>
        {
            session.SessionId = sessionId;
        };

        cli.OnCompleted += result =>
        {
            if (result.SessionId is not null)
                session.SessionId = result.SessionId;

            var fullText = session.Response.ToString();
            var analysisResult = ParseAnalysis(fullText);
            var savedSessionId = session.SessionId;
            analysisResult.SessionId = savedSessionId;

            // Only fire event if we still own the session (not stopped externally)
            if (_sessions.TryRemove(featureId, out _))
            {
                session.Cli.StopSession();
                OnAnalysisComplete?.Invoke(featureId, analysisResult);
            }
        };

        cli.OnError += error =>
        {
            // Only fire event if we still own the session
            if (_sessions.TryRemove(featureId, out _))
            {
                session.Cli.StopSession();
                OnAnalysisError?.Invoke(featureId, error);
            }
        };

        cli.OnControlRequest += (requestId, toolName, toolUseId, input) =>
        {
            if (toolName == "AskUserQuestion")
            {
                HandleAskUserQuestion(session, requestId, toolUseId, input);
            }
            else
            {
                // Allow read-only tools, deny everything else
                var allowed = toolName is "Read" or "Glob" or "Grep" or "WebFetch"
                    or "WebSearch";
                cli.SendControlResponse(requestId, allowed ? "allow" : "deny",
                    toolUseId: toolUseId);
            }
        };
    }

    private void HandleAskUserQuestion(AnalyzerSession session, string requestId,
        string toolUseId, JsonElement input)
    {
        session.PendingRequestId = requestId;
        session.PendingToolUseId = toolUseId;

        var questionText = ExtractQuestionText(input);
        session.PendingQuestionText = questionText;

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

        return "Analyzer needs your input";
    }

    /// <summary>
    /// Parse the analyzer's response to extract the JSON analysis result.
    /// </summary>
    internal static AnalysisResult ParseAnalysis(string text)
    {
        var jsonText = JsonBlockExtractor.Extract(text);
        if (jsonText is null)
            return new AnalysisResult { Verdict = "needs_discussion", Summary = text, RawText = text };

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            var result = new AnalysisResult
            {
                Verdict = root.TryGetProperty("verdict", out var v) ? v.GetString() ?? "needs_discussion" : "needs_discussion",
                Title = root.TryGetProperty("title", out var t) ? t.GetString() : null,
                Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                Reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null,
                RawText = text
            };

            if (root.TryGetProperty("affectedProjects", out var projects)
                && projects.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in projects.EnumerateArray())
                {
                    var name = p.GetString();
                    if (!string.IsNullOrEmpty(name))
                        result.AffectedProjects.Add(name);
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return new AnalysisResult { Verdict = "needs_discussion", Summary = text, RawText = text };
        }
    }

    private class AnalyzerSession
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

/// <summary>
/// Result of an idea analysis by the AnalyzerService.
/// </summary>
public class AnalysisResult
{
    public string Verdict { get; set; } = "needs_discussion";
    public string? Title { get; set; }
    public string Summary { get; set; } = "";
    public string? Reason { get; set; }
    public List<string> AffectedProjects { get; set; } = [];
    public string? SessionId { get; set; }
    public string RawText { get; set; } = "";
}
