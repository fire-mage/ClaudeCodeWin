namespace ClaudeCodeWin.Models;

public class TeamNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string SourceRole { get; set; } = "";
    public string? FeatureId { get; set; }
    public string? FeatureTitle { get; set; }
    public string ProjectPath { get; set; } = "";
    public string Message { get; set; } = "";
    public bool IsRead { get; set; }
}
