namespace ClaudeCodeWin.Models;

public class QueuedMessage
{
    public string Text { get; set; }
    public List<FileAttachment>? Attachments { get; set; }

    public QueuedMessage(string text, List<FileAttachment>? attachments = null)
    {
        Text = text;
        Attachments = attachments;
    }
}
