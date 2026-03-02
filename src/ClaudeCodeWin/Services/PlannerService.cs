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
    private readonly ConcurrentDictionary<string, DiscussionSession> _discussionSessions = new();
    private string? _claudeExePath;
    private string? _workingDirectory;

    public TeamNotesService? TeamNotesService { get; set; }

    public event Action<string, string, string?, List<BacklogPhase>>? OnPlanReady; // featureId, title, sessionId, phases
    public event Action<string, string>? OnQuestionAsked; // featureId, question
    public event Action<string, string>? OnPlannerError; // featureId, error
    public event Action<string, string>? OnPlannerTextDelta; // featureId, text
    public event Action<string, PlanDiscussionResult>? OnDiscussionReady; // featureId, result
    public event Action<string, string>? OnDiscussionError; // featureId, error

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
            FeatureId = feature.Id,
            FeatureTitle = feature.Title ?? feature.RawIdea,
            ProjectPath = feature.ProjectPath
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
    /// Stop all active planning and discussion sessions.
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

        while (!_discussionSessions.IsEmpty)
        {
            foreach (var key in _discussionSessions.Keys)
            {
                if (_discussionSessions.TryRemove(key, out var ds))
                    ds.Cli.StopSession();
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

            // Extract notes before firing completion
            if (TeamNotesService is { } notesService && session.ProjectPath is not null)
            {
                var notes = TeamNotesDetector.ExtractNotes(fullText);
                if (notes.Count > 0)
                    notesService.AddNotes(session.ProjectPath, "planner", featureId, session.FeatureTitle, notes);
            }

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
    /// Start a discussion session for a feature — generates 3-5 questions about the plan.
    /// Creates a NEW CLI session (not reusing the planner session).
    /// </summary>
    public void StartDiscussion(BacklogFeature feature)
    {
        var (systemPrompt, userMessage) = TeamPrompts.BuildPlanDiscussionPrompt(feature);

        var cli = new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = feature.ProjectPath ?? _workingDirectory,
            SystemPrompt = systemPrompt,
            DangerouslySkipPermissions = false,
            ModelOverride = TeamPrompts.TeamModelId
        };

        var ds = new DiscussionSession
        {
            Cli = cli,
            FeatureId = feature.Id
        };

        if (!_discussionSessions.TryAdd(feature.Id, ds))
        {
            cli.StopSession();
            return;
        }

        WireDiscussionEvents(ds);
        cli.SendMessage(userMessage);
    }

    /// <summary>
    /// Submit user's answers to discussion questions and refine the plan.
    /// Creates a new CLI session for refinement.
    /// </summary>
    public void SubmitDiscussionAnswers(string featureId, BacklogFeature feature,
        List<(string question, string answer)> answers)
    {
        // Stop any existing discussion session for this feature
        StopDiscussion(featureId);

        var (systemPrompt, userMessage) = TeamPrompts.BuildPlanRefinementPrompt(feature, answers);

        var cli = new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = feature.ProjectPath ?? _workingDirectory,
            SystemPrompt = systemPrompt,
            DangerouslySkipPermissions = false,
            ModelOverride = TeamPrompts.TeamModelId
        };

        var ds = new DiscussionSession
        {
            Cli = cli,
            FeatureId = featureId,
            IsRefinement = true
        };

        // Use TryAdd — if another refinement is already running, skip
        if (!_discussionSessions.TryAdd(featureId, ds))
        {
            cli.StopSession();
            return;
        }

        WireDiscussionEvents(ds);
        cli.SendMessage(userMessage);
    }

    /// <summary>
    /// Stop/cancel the discussion CLI session for a feature.
    /// </summary>
    public void StopDiscussion(string featureId)
    {
        if (_discussionSessions.TryRemove(featureId, out var ds))
            ds.Cli.StopSession();
    }

    public bool IsDiscussing(string featureId) => _discussionSessions.ContainsKey(featureId);

    private void WireDiscussionEvents(DiscussionSession ds)
    {
        var cli = ds.Cli;
        var featureId = ds.FeatureId;

        // No lock needed: OnTextDelta/OnCompleted are sequential on the same stream-reading thread.
        // StopSession kills the process, preventing further events.
        cli.OnTextDelta += text => ds.Response.Append(text);

        cli.OnCompleted += result =>
        {
            var fullText = ds.Response.ToString();

            // Compare removed session to guard against race with StopDiscussion+StartDiscussion
            if (_discussionSessions.TryRemove(featureId, out var completedSession))
            {
                if (completedSession != ds)
                    _discussionSessions.TryAdd(featureId, completedSession); // restore new session
            }
            // Always clean up our own CLI process (idempotent for already-exited processes)
            ds.Cli.StopSession();

            if (ds.IsRefinement)
            {
                // Parse as a plan (same format as PlannerSystemPrompt)
                var (title, phases) = ParsePlan(fullText);
                if (phases.Count > 0)
                    OnPlanReady?.Invoke(featureId, title, null, phases);
                else
                    OnDiscussionError?.Invoke(featureId, "Refinement did not produce a valid plan");
            }
            else
            {
                // Parse as discussion questions
                var questions = ParseDiscussionQuestions(fullText);
                if (questions != null && questions.Questions.Count > 0)
                    OnDiscussionReady?.Invoke(featureId, questions);
                else
                    OnDiscussionError?.Invoke(featureId, "Failed to generate discussion questions");
            }
        };

        cli.OnError += error =>
        {
            if (_discussionSessions.TryRemove(featureId, out var errorSession))
            {
                if (errorSession != ds)
                    _discussionSessions.TryAdd(featureId, errorSession); // restore new session
            }
            ds.Cli.StopSession();
            OnDiscussionError?.Invoke(featureId, error);
        };

        // Allow read-only tools only, deny everything else (no AskUserQuestion)
        cli.OnControlRequest += (requestId, toolName, toolUseId, _) =>
        {
            var allowed = toolName is "Read" or "Glob" or "Grep" or "WebFetch" or "WebSearch";
            cli.SendControlResponse(requestId, allowed ? "allow" : "deny",
                toolUseId: toolUseId);
        };
    }

    /// <summary>
    /// Parse the discussion response to extract questions with suggested answers.
    /// </summary>
    internal static PlanDiscussionResult? ParseDiscussionQuestions(string text)
    {
        var jsonText = JsonBlockExtractor.Extract(text, "questions");
        if (jsonText is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            if (!root.TryGetProperty("questions", out var questionsArr)
                || questionsArr.ValueKind != JsonValueKind.Array)
                return null;

            var result = new PlanDiscussionResult();
            var index = 0;

            foreach (var q in questionsArr.EnumerateArray())
            {
                var question = new PlanDiscussionQuestion
                {
                    Index = index++,
                    Question = q.TryGetProperty("question", out var qt)
                        ? qt.GetString() ?? "" : ""
                };

                if (q.TryGetProperty("suggestedAnswers", out var sa)
                    && sa.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in sa.EnumerateArray())
                    {
                        var answer = a.GetString();
                        if (!string.IsNullOrEmpty(answer))
                            question.SuggestedAnswers.Add(answer);
                    }
                }

                if (!string.IsNullOrEmpty(question.Question))
                    result.Questions.Add(question);
            }

            return result.Questions.Count > 0 ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
        public string? FeatureTitle;
        public string? ProjectPath;
        public StringBuilder Response = new();
        public string? SessionId;
        public string? PendingRequestId;
        public string? PendingToolUseId;
        public string? PendingQuestionText;
        public string? PendingQuestionsJson;
    }

    private class DiscussionSession
    {
        public required ClaudeCliService Cli;
        public required string FeatureId;
        public StringBuilder Response = new();
        public bool IsRefinement;
    }
}
