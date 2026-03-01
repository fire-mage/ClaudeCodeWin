namespace ClaudeCodeWin.Models;

public class IdeasDocument
{
    public string ProjectPath { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime LastSavedAt { get; set; } = DateTime.Now;
}
