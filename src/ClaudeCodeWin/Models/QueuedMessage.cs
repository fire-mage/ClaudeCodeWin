namespace ClaudeCodeWin.Models;

public class QueuedMessage
{
    public string Text { get; set; }

    public QueuedMessage(string text)
    {
        Text = text;
    }
}
