using System.Text.Json;
using System.Windows;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    private void HandleControlRequest(string requestId, string toolName, string toolUseId, JsonElement input)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (toolName == "ExitPlanMode")
            {
                HandleExitPlanModeControl(requestId, toolUseId);
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

    private void HandleExitPlanModeControl(string requestId, string toolUseId)
    {
        _exitPlanModeAutoCount++;

        if (AutoConfirmEnabled && _exitPlanModeAutoCount <= 2)
        {
            _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            Messages.Add(new MessageViewModel(MessageRole.System, "Plan approved automatically."));
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

            var questionMsg = new MessageViewModel(MessageRole.System, "Exit plan mode?")
            {
                QuestionDisplay = new QuestionDisplayModel
                {
                    QuestionText = "Claude wants to exit plan mode and start implementing. Approve?",
                    Options =
                    [
                        new QuestionOption { Label = "Yes, go ahead", Description = "Approve plan and start implementation" },
                        new QuestionOption { Label = "No, keep planning", Description = "Stay in plan mode" }
                    ]
                }
            };
            Messages.Add(questionMsg);
            UpdateCta(CtaState.AnswerQuestion);
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

        // ExitPlanMode — simple allow/deny
        if (_pendingQuestionInput is null)
        {
            _pendingControlRequestId = null;
            _pendingControlToolUseId = null;

            if (answer == "Yes, go ahead")
                _cliService.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            else
                _cliService.SendControlResponse(requestId, "deny", errorMessage: "User chose to keep planning");

            Messages.Add(new MessageViewModel(MessageRole.User, answer));
            UpdateCta(CtaState.Processing);
            return;
        }

        // AskUserQuestion — collect answers, then build updatedInput with questions+answers
        // Find the question text for this answer index
        try
        {
            var input = _pendingQuestionInput.Value;
            if (input.TryGetProperty("questions", out var questionsArr)
                && questionsArr.ValueKind == JsonValueKind.Array)
            {
                var idx = _pendingQuestionAnswers.Count;
                var questions = questionsArr.EnumerateArray().ToList();
                if (idx < questions.Count)
                {
                    var questionText = questions[idx].TryGetProperty("question", out var qt)
                        ? qt.GetString() ?? "" : "";
                    _pendingQuestionAnswers.Add((questionText, answer));
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

            // Show user's answers
            foreach (var (q, a) in _pendingQuestionAnswers)
                Messages.Add(new MessageViewModel(MessageRole.User, a));

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
            if (msg.QuestionDisplay is not null)
                msg.QuestionDisplay = null;
        }
    }
}
