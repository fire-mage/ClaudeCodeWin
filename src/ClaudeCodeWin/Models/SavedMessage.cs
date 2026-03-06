namespace ClaudeCodeWin.Models;

public class SavedMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = "";
    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>Short preview for display in lists (first line, max 80 chars).</summary>
    public string Preview
    {
        get
        {
            var firstLine = Text.Split('\n', 2)[0].Trim();
            return firstLine.Length > 80 ? firstLine[..77] + "..." : firstLine;
        }
    }
}
