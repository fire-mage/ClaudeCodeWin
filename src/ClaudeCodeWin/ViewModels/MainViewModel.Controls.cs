using System.Text;
using System.Text.Json;
using System.Windows;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    private void HandleControlRequest(string requestId, string toolName, string toolUseId, JsonElement input)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
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
                // Auto-approve other tool permission requests
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
                }
                else
                {
                    Messages.Add(new MessageViewModel(MessageRole.System, $"Claude asked: {question}"));
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

        // Clear question buttons from all messages (they've been answered)
        ClearQuestionDisplays();

        // ExitPlanMode — simple allow/deny/reset
        if (_pendingQuestionInput is null)
        {
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
                }
            }
        }
        catch (JsonException) { }

        if (_pendingQuestionAnswers.Count >= _pendingQuestionCount)
        {
            // All answers collected — send control_response
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
}
