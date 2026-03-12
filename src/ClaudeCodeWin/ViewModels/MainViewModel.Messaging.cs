using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

/// <summary>
/// Messaging partial — thin delegates to ActiveChatSession.
/// All messaging logic now lives in ChatSessionViewModel.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Add a file attachment to the active chat session.</summary>
    public void AddAttachment(FileAttachment attachment) => ActiveChatSession?.AddAttachment(attachment);

    /// <summary>Send a message directly (used by notepad send, FinalizeActions, etc.).</summary>
    internal async Task SendDirectAsync(string text, List<FileAttachment>? attachments,
        List<MessageContentPart>? contentParts = null)
    {
        if (ActiveChatSession != null)
            await ActiveChatSession.SendDirectAsync(text, attachments, contentParts: contentParts);
    }
}
