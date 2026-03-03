using System.Collections.ObjectModel;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

/// <summary>
/// Reusable assembler that converts streaming CLI events into MessageViewModel
/// objects in an ObservableCollection. Used by both main chat and team chat.
/// All methods must be called on the UI thread.
/// </summary>
public class ChatMessageAssembler
{
    private MessageViewModel? _currentAssistantMessage;
    private bool _isFirstDelta;
    private bool _hadToolsSinceLastText;

    public ObservableCollection<MessageViewModel> Messages { get; }

    /// <summary>Current assistant message being assembled (null between turns).</summary>
    public MessageViewModel? CurrentMessage => _currentAssistantMessage;

    public ChatMessageAssembler(ObservableCollection<MessageViewModel> messages)
    {
        Messages = messages;
    }

    /// <summary>Start a new assistant message (call before sending to CLI).</summary>
    public MessageViewModel BeginAssistantMessage(string? reviewerLabel = null)
    {
        // Finalize any incomplete previous message to prevent stale streaming indicators
        if (_currentAssistantMessage is not null)
        {
            _currentAssistantMessage.IsStreaming = false;
            _currentAssistantMessage.IsThinking = false;
        }

        _currentAssistantMessage = new MessageViewModel(MessageRole.Assistant)
        {
            IsStreaming = true,
            IsThinking = true,
            ReviewerLabel = reviewerLabel
        };
        _isFirstDelta = true;
        _hadToolsSinceLastText = false;
        Messages.Add(_currentAssistantMessage);
        return _currentAssistantMessage;
    }

    /// <summary>Handle text content_block start — split into new bubble if tools intervened.</summary>
    public void HandleTextBlockStart()
    {
        if (_currentAssistantMessage is null) return;

        if (_hadToolsSinceLastText)
        {
            _currentAssistantMessage.IsStreaming = false;
            _currentAssistantMessage = new MessageViewModel(MessageRole.Assistant) { IsStreaming = true };
            Messages.Add(_currentAssistantMessage);
            _hadToolsSinceLastText = false;
            _isFirstDelta = true;
        }
    }

    /// <summary>Handle text delta — first delta clears thinking, subsequent appends.</summary>
    public void HandleTextDelta(string text)
    {
        if (_currentAssistantMessage is null) return;

        if (_isFirstDelta)
        {
            _isFirstDelta = false;
            _currentAssistantMessage.IsThinking = false;
            _currentAssistantMessage.Text = text;
        }
        else
        {
            _currentAssistantMessage.Text += text;
        }
    }

    /// <summary>Handle thinking delta — append to ThinkingText.</summary>
    public void HandleThinkingDelta(string text)
    {
        if (_currentAssistantMessage is null) return;
        _currentAssistantMessage.ThinkingText += text;
        _currentAssistantMessage.ResetThinkingTimer();
    }

    /// <summary>Handle tool use started — add or update ToolUseViewModel.</summary>
    public void HandleToolUseStarted(string toolName, string toolUseId, string input)
    {
        if (_currentAssistantMessage is null) return;

        _hadToolsSinceLastText = true;

        if (_isFirstDelta)
        {
            _isFirstDelta = false;
            _currentAssistantMessage.IsThinking = false;
        }

        // Check if this tool use already exists (update with complete input)
        var existing = _currentAssistantMessage.ToolUses
            .FirstOrDefault(t => t.ToolUseId == toolUseId && !string.IsNullOrEmpty(toolUseId));

        if (existing is not null)
        {
            existing.UpdateInput(input);
        }
        else
        {
            _currentAssistantMessage.ToolUses.Add(new ToolUseViewModel(toolName, toolUseId, input));
        }
    }

    /// <summary>Handle tool result — set result content on matching tool use.</summary>
    public void HandleToolResult(string toolName, string toolUseId, string content)
    {
        if (_currentAssistantMessage is null) return;

        var tool = _currentAssistantMessage.ToolUses
            .FirstOrDefault(t => t.ToolUseId == toolUseId)
            ?? _currentAssistantMessage.ToolUses.LastOrDefault(t => t.ToolName == toolName);

        if (tool is not null)
        {
            tool.ResultContent = content.Length > 5000
                ? content[..5000] + $"\n\n... ({content.Length:N0} chars total)"
                : content;
        }
    }

    /// <summary>Finalize current assistant message (IsStreaming=false, extract summary).</summary>
    public void HandleCompleted()
    {
        if (_currentAssistantMessage is not null)
        {
            _currentAssistantMessage.IsStreaming = false;
            _currentAssistantMessage.IsThinking = false;
            _currentAssistantMessage.ExtractCompletionSummary();
        }

        _currentAssistantMessage = null;
        _hadToolsSinceLastText = false;
    }

    /// <summary>Handle error — add error text to current message or as system message.</summary>
    public void HandleError(string error)
    {
        if (_currentAssistantMessage is not null)
        {
            _currentAssistantMessage.IsStreaming = false;
            _currentAssistantMessage.IsThinking = false;
            if (string.IsNullOrEmpty(_currentAssistantMessage.Text))
                _currentAssistantMessage.Text = $"Error: {error}";
            else
                _currentAssistantMessage.Text += $"\n\nError: {error}";
        }
        else
        {
            Messages.Add(new MessageViewModel(MessageRole.System, $"Error: {error}"));
        }
    }

    /// <summary>Add a system message.</summary>
    public void AddSystemMessage(string text)
    {
        Messages.Add(new MessageViewModel(MessageRole.System, text));
    }

    /// <summary>Add a user message.</summary>
    public MessageViewModel AddUserMessage(string text, List<FileAttachment>? attachments = null)
    {
        var msg = new MessageViewModel(MessageRole.User, text);
        if (attachments is not null)
            msg.Attachments = attachments;
        Messages.Add(msg);
        return msg;
    }

    /// <summary>Reset assembler state (e.g., on new session). Does NOT clear messages.</summary>
    public void Reset()
    {
        _currentAssistantMessage = null;
        _isFirstDelta = true;
        _hadToolsSinceLastText = false;
    }

    /// <summary>Clear all messages and reset state.</summary>
    public void ClearMessages()
    {
        Messages.Clear();
        Reset();
    }

    /// <summary>Clear all active thinking/streaming indicators on all messages.</summary>
    public void ClearAllThinking()
    {
        foreach (var msg in Messages)
        {
            if (msg.IsThinking) msg.IsThinking = false;
            if (msg.IsStreaming) msg.IsStreaming = false;
        }
    }
}
