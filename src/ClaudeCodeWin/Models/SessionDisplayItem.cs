namespace ClaudeCodeWin.Models;

/// <summary>
/// Display model for session/chat list items in dialogs.
/// </summary>
public class SessionDisplayItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? ProjectPath { get; set; }
    public string ProjectName { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public int MessageCount { get; set; }
}
