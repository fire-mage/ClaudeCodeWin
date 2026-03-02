using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    private const int ConflictPauseTimeoutSeconds = 30;

    private void HandleControlRequest(string requestId, string toolName, string toolUseId, JsonElement input)
    {
        RunOnUI(() =>
        {
            if (toolName == "ExitPlanMode")
            {
                HandleExitPlanModeControl(requestId, toolUseId, input);
            }
            else if (toolName == "AskUserQuestion")
            {
                HandleAskUserQuestionControl(requestId, toolUseId, input);
            }
            else
            {
                // Check for file write conflicts with team.
                // Known limitation: Bash tool calls that modify files (sed, echo >, mv, cat >)
                // bypass this check — parsing arbitrary shell commands is impractical.
                // Claude frequently uses Bash for file modifications, so conflicts may go undetected.
                if (toolName is "Write" or "Edit" or "NotebookEdit")
                {
                    var filePath = ExtractFilePathFromToolInput(input);
                    if (filePath != null && IsFileConflictWithTeam(filePath))
                    {
                        _ = HandleConflictPauseAsync(requestId, toolUseId, filePath);
                        return;
                    }
                }

                _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            }
        });
    }

    private void HandleExitPlanModeControl(string requestId, string toolUseId, JsonElement input)
    {
        _exitPlanModeAutoCount++;

        var permissions = ExtractAllowedPrompts(input);
        var ctx = ContextUsageText;

        DiagnosticLogger.Log("EXIT_PLAN_MODE",
            $"permissions=[{permissions}] ctx={ctx} input={input.GetRawText()}");

        if (AutoConfirmEnabled && _exitPlanModeAutoCount <= 2)
        {
            _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);

            var msg = string.IsNullOrEmpty(permissions)
                ? $"Plan approved automatically. [{ctx}]"
                : $"Plan approved automatically.\nPermissions: {permissions}\n[{ctx}]";
            Messages.Add(new MessageViewModel(MessageRole.System, msg));
        }
        else
        {
            if (AutoConfirmEnabled && _exitPlanModeAutoCount > 2)
            {
                AutoConfirmEnabled = false;
                Messages.Add(new MessageViewModel(MessageRole.System,
                    "Auto-confirm disabled (loop detected). Please confirm manually."));
            }

            // Store pending request for manual confirmation
            _pendingControlRequestId = requestId;
            _pendingControlToolUseId = toolUseId;

            var questionText = "Claude wants to exit plan mode and start implementing.";
            if (!string.IsNullOrEmpty(permissions))
                questionText += $"\nPermissions: {permissions}";
            questionText += $"\n[{ctx}]";

            var questionMsg = new MessageViewModel(MessageRole.System, "Exit plan mode?")
            {
                QuestionDisplay = new QuestionDisplayModel
                {
                    QuestionText = questionText,
                    Options =
                    [
                        new QuestionOption { Label = "Yes, go ahead", Description = "Approve plan and start implementation" },
                        new QuestionOption { Label = "No, keep planning", Description = "Stay in plan mode" },
                        new QuestionOption { Label = "New session + plan", Description = "Reset context and continue with plan only" }
                    ]
                }
            };
            Messages.Add(questionMsg);
            UpdateCta(CtaState.AnswerQuestion);
        }
    }

    private static string ExtractAllowedPrompts(JsonElement input)
    {
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return "";

        try
        {
            if (!input.TryGetProperty("allowedPrompts", out var prompts)
                || prompts.ValueKind != JsonValueKind.Array)
                return "";

            var sb = new StringBuilder();
            foreach (var p in prompts.EnumerateArray())
            {
                var tool = p.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : "";
                var prompt = p.TryGetProperty("prompt", out var pr) ? pr.GetString() ?? "" : "";
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"{tool}({prompt})");
            }
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    private void HandleAskUserQuestionControl(string requestId, string toolUseId, JsonElement input)
    {
        _pendingControlRequestId = requestId;
        _pendingControlToolUseId = toolUseId;
        _pendingQuestionInput = input.ValueKind != JsonValueKind.Undefined ? input : null;
        _pendingQuestionAnswers.Clear();
        _pendingQuestionMessages.Clear();

        try
        {
            if (!input.TryGetProperty("questions", out var questionsArr)
                || questionsArr.ValueKind != JsonValueKind.Array)
                return;

            _pendingQuestionCount = questionsArr.GetArrayLength();

            foreach (var q in questionsArr.EnumerateArray())
            {
                var question = q.TryGetProperty("question", out var qText) ? qText.GetString() ?? "" : "";
                var options = new List<QuestionOption>();

                if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var opt in opts.EnumerateArray())
                    {
                        options.Add(new QuestionOption
                        {
                            Label = opt.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                            Description = opt.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""
                        });
                    }
                }

                if (options.Count > 0)
                {
                    var questionMsg = new MessageViewModel(MessageRole.System, question)
                    {
                        QuestionDisplay = new QuestionDisplayModel
                        {
                            QuestionText = question,
                            Options = options
                        }
                    };
                    Messages.Add(questionMsg);
                    _pendingQuestionMessages.Add(questionMsg);
                }
                else
                {
                    Messages.Add(new MessageViewModel(MessageRole.System, $"Claude asked: {question}"));
                    _pendingQuestionMessages.Add(null!); // placeholder to keep indices aligned
                }
            }

            UpdateCta(CtaState.AnswerQuestion);
        }
        catch (JsonException) { }
    }

    private void HandleControlAnswer(string answer)
    {
        var requestId = _pendingControlRequestId!;
        var toolUseId = _pendingControlToolUseId;

        // ExitPlanMode — simple allow/deny/reset
        if (_pendingQuestionInput is null)
        {
            ClearQuestionDisplays();
            _pendingControlRequestId = null;
            _pendingControlToolUseId = null;

            if (answer == "Yes, go ahead")
            {
                _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            }
            else if (answer == "New session + plan")
            {
                _cliService.SendControlResponse(requestId, "deny",
                    errorMessage: "User chose to reset context and continue with plan only");
                Messages.Add(new MessageViewModel(MessageRole.User, answer));
                StartNewSession();
                return;
            }
            else
            {
                _cliService.SendControlResponse(requestId, "deny",
                    errorMessage: "User chose to keep planning");
            }

            Messages.Add(new MessageViewModel(MessageRole.User, answer));
            UpdateCta(CtaState.Processing);
            return;
        }

        // AskUserQuestion — collect answers, then build updatedInput with questions+answers
        try
        {
            var input = _pendingQuestionInput.Value;
            if (input.TryGetProperty("questions", out var questionsArr)
                && questionsArr.ValueKind == JsonValueKind.Array)
            {
                var questions = questionsArr.EnumerateArray().ToList();

                // Check if this answer matches a known option (button click)
                // vs free-text input that should answer all remaining questions
                var idx = _pendingQuestionAnswers.Count;
                var isButtonClick = idx < questions.Count
                    && questions[idx].TryGetProperty("options", out var opts)
                    && opts.ValueKind == JsonValueKind.Array
                    && opts.EnumerateArray().Any(o =>
                        o.TryGetProperty("label", out var l) && l.GetString() == answer);

                if (isButtonClick)
                {
                    // Single button click — answer one question at a time
                    var questionText = questions[idx].TryGetProperty("question", out var qt)
                        ? qt.GetString() ?? "" : "";
                    _pendingQuestionAnswers.Add((questionText, answer));

                    // Disable only this question's buttons
                    if (idx < _pendingQuestionMessages.Count
                        && _pendingQuestionMessages[idx]?.QuestionDisplay is { } answered)
                        answered.IsAnswered = true;
                }
                else
                {
                    // Free-text input — answer all remaining questions at once
                    for (var i = _pendingQuestionAnswers.Count; i < questions.Count; i++)
                    {
                        var questionText = questions[i].TryGetProperty("question", out var qt)
                            ? qt.GetString() ?? "" : "";
                        _pendingQuestionAnswers.Add((questionText, answer));
                    }
                    // Disable all remaining question buttons
                    ClearQuestionDisplays();
                }
            }
        }
        catch (JsonException) { }

        if (_pendingQuestionAnswers.Count >= _pendingQuestionCount)
        {
            // All answers collected — disable any remaining buttons and send control_response
            ClearQuestionDisplays();

            var answersDict = new Dictionary<string, string>();
            foreach (var (q, a) in _pendingQuestionAnswers)
                answersDict[q] = a;

            // Build updatedInput JSON: { "questions": [...original...], "answers": {...} }
            var questionsJson = "[]";
            try
            {
                if (_pendingQuestionInput?.TryGetProperty("questions", out var qa) == true)
                    questionsJson = qa.GetRawText();
            }
            catch { }

            var answersJson = JsonSerializer.Serialize(answersDict);
            var updatedInputJson = "{\"questions\":" + questionsJson + ",\"answers\":" + answersJson + "}";

            _cliService.SendControlResponse(requestId, "allow",
                updatedInputJson: updatedInputJson, toolUseId: toolUseId);

            // Show user's answers (deduplicate identical answers from free-text input)
            var shownAnswers = new HashSet<string>();
            foreach (var (q, a) in _pendingQuestionAnswers)
            {
                if (shownAnswers.Add(a))
                    Messages.Add(new MessageViewModel(MessageRole.User, a));
            }

            _pendingControlRequestId = null;
            _pendingControlToolUseId = null;
            _pendingQuestionInput = null;
            _pendingQuestionAnswers.Clear();
            _pendingQuestionMessages.Clear();
            _pendingQuestionCount = 0;
            UpdateCta(CtaState.Processing);
        }
    }

    private void ClearQuestionDisplays()
    {
        foreach (var msg in Messages)
        {
            if (msg.QuestionDisplay is { IsAnswered: false })
                msg.QuestionDisplay.IsAnswered = true;
        }
    }

    private static string? ExtractFilePathFromToolInput(JsonElement input)
    {
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        if (input.TryGetProperty("file_path", out var fp) && fp.ValueKind == JsonValueKind.String)
            return fp.GetString();

        if (input.TryGetProperty("notebook_path", out var np) && np.ValueKind == JsonValueKind.String)
            return np.GetString();

        return null;
    }

    private bool IsFileConflictWithTeam(string filePath)
    {
        // Only check when team is actively running or waiting for work
        var orch = _orchestratorService;
        if (orch == null) return false;
        var state = orch.State;
        if (state is not (OrchestratorState.Running or OrchestratorState.WaitingForWork))
            return false;

        // Canonicalize input path; if the input itself is malformed, skip entirely
        string normalizedPath;
        try { normalizedPath = Path.GetFullPath(filePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            DiagnosticLogger.Log("CONFLICT_CHECK_ERROR", $"Bad input path: {ex.Message}");
            return false;
        }

        // Check active session's changed files
        var activeFiles = orch.GetActiveSessionChangedFiles();
        if (IsPathInList(activeFiles, normalizedPath))
            return true;

        // Check changed files from InProgress features in the backlog
        // Only when team is actively Running — WaitingForWork may have stale InProgress features
        if (state == OrchestratorState.Running && !string.IsNullOrEmpty(WorkingDirectory))
        {
            var inProgressFeatures = _backlogService.GetFeaturesByStatus(WorkingDirectory, FeatureStatus.InProgress);
            foreach (var feature in inProgressFeatures)
            {
                foreach (var phase in feature.Phases)
                {
                    if (IsPathInList(phase.ChangedFiles, normalizedPath))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if normalizedPath matches any path in the list.
    /// Malformed entries are skipped so one bad path doesn't abort the entire check.
    /// </summary>
    private static bool IsPathInList(IEnumerable<string> paths, string normalizedPath)
    {
        foreach (var f in paths)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(f), normalizedPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Skip malformed path, continue checking others
                DiagnosticLogger.Log("CONFLICT_CHECK_SKIP", $"Skipping malformed path '{f}': {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>
    /// Pauses team orchestrator when a Write/Edit/NotebookEdit conflicts with team files.
    /// Called as fire-and-forget: _ = HandleConflictPauseAsync(...).
    /// Exceptions are caught internally — safe despite not being awaited.
    /// </summary>
    private async Task HandleConflictPauseAsync(string requestId, string toolUseId, string filePath)
    {
        // Guard: if already handling a conflict pause, just allow this tool immediately
        if (_conflictPauseCts != null)
        {
            _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            return;
        }

        _teamPausedForConflict = true;
        _conflictEditAnyway = false;
        _pendingConflictRequestId = requestId;
        _pendingConflictToolUseId = toolUseId;
        ConflictBannerText = $"Team is editing {Path.GetFileName(filePath)} \u2014 pausing team...";
        OnPropertyChanged(nameof(IsConflictBannerVisible));
        OnPropertyChanged(nameof(IsConflictActionable));

        _conflictPauseCts = new CancellationTokenSource(TimeSpan.FromSeconds(ConflictPauseTimeoutSeconds));
        var orch = _orchestratorService;
        if (orch == null)
        {
            // Orchestrator disposed between conflict check and pause — allow the tool
            ClearConflictPauseState();
            _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            return;
        }

        var responseSent = false;
        try
        {
            await orch.SoftPauseAsync(_conflictPauseCts.Token);

            // Team confirmed paused — allow the tool
            ConflictBannerText = "Team paused \u2014 working...";
            _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            responseSent = true;
        }
        catch (OperationCanceledException)
        {
            // Capture as local — HandleCancelConflict may have nulled the field
            var pendingReqId = _pendingConflictRequestId;

            if (_conflictEditAnyway)
            {
                // User clicked "Edit anyway" — allow the tool, clear pause, resume team
                if (!responseSent && _cliService.IsProcessRunning)
                    _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
                responseSent = true;
                ClearConflictPauseState();
            }
            else
            {
                // Timeout or ResumeTeamAfterConflict cancelled — allow anyway
                // Only clean up if ResumeTeamAfterConflict hasn't already done so
                if (_teamPausedForConflict)
                    ClearConflictPauseState();
                // Only send if CLI is still alive and not denied by HandleCancelConflict
                if (!responseSent && _cliService.IsProcessRunning && pendingReqId != null)
                    _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            }
        }
        catch (Exception ex)
        {
            // Capture as local — HandleCancelConflict may have nulled the field
            var pendingReqId = _pendingConflictRequestId;

            DiagnosticLogger.Log("CONFLICT_PAUSE_ERROR", ex.Message);
            if (_teamPausedForConflict)
                ClearConflictPauseState();
            if (!responseSent && _cliService.IsProcessRunning && pendingReqId != null)
                _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
        }
        finally
        {
            // Always release the CTS so the guard check allows future conflict detection
            if (_conflictPauseCts != null)
            {
                _conflictPauseCts.Dispose();
                _conflictPauseCts = null;
            }
            _pendingConflictRequestId = null;
            _pendingConflictToolUseId = null;
            OnPropertyChanged(nameof(IsConflictActionable));

            // Safety net: if banner still shows after success path ("Team paused — working...")
            // and HandleCompleted/HandleError never fires (CLI crash, process killed), auto-clear
            // the banner after 2 minutes so it doesn't stay visible forever.
            if (_teamPausedForConflict && !string.IsNullOrEmpty(ConflictBannerText))
            {
                _conflictBannerClearTimer?.Stop();
                _conflictBannerClearTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(2)
                };
                _conflictBannerClearTimer.Tick += (_, _) =>
                {
                    _conflictBannerClearTimer?.Stop();
                    _conflictBannerClearTimer = null;
                    if (_teamPausedForConflict)
                    {
                        DiagnosticLogger.Log("CONFLICT_BANNER_TIMEOUT",
                            "Conflict banner auto-cleared after 2 min safety timeout.");
                        ClearConflictPauseState();
                    }
                };
                _conflictBannerClearTimer.Start();
            }
        }
    }

    private void ClearConflictPauseState()
    {
        ConflictBannerText = "";
        _teamPausedForConflict = false;
        OnPropertyChanged(nameof(IsConflictBannerVisible));
        _orchestratorService?.ClearPendingSoftPause();
        _orchestratorService?.ResumeIfSoftPaused();
    }

    private void HandleEditAnyway()
    {
        _conflictEditAnyway = true;
        _conflictPauseCts?.Cancel();
    }

    private void HandleCancelConflict()
    {
        var reqId = _pendingConflictRequestId;
        var toolId = _pendingConflictToolUseId;

        // Null out pending IDs first — signals catch block in HandleConflictPauseAsync
        // not to send a duplicate 'allow' response after we send 'deny' here
        _pendingConflictRequestId = null;
        _pendingConflictToolUseId = null;

        // Cancel the pause wait
        _conflictPauseCts?.Cancel();

        // Fire-and-forget deny — written to stdin before CancelProcessing() kills the CLI.
        // The CLI may not process it, but the session is being destroyed anyway.
        if (reqId != null && _cliService.IsProcessRunning)
            _cliService.SendControlResponse(reqId, "deny", toolUseId: toolId,
                errorMessage: "User cancelled due to file conflict");

        // CancelProcessing() calls ResumeTeamAfterConflict() which handles all cleanup:
        // clears _teamPausedForConflict, ConflictBannerText, disposes CTS, resumes team
        CancelProcessing();
    }
}
