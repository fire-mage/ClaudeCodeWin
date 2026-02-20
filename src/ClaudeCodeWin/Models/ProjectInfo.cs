namespace ClaudeCodeWin.Models;

public class ProjectInfo
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? GitRemoteUrl { get; set; }
    public string? TechStack { get; set; }
    public DateTime LastOpened { get; set; }
    public DateTime RegisteredAt { get; set; }
    public string? Notes { get; set; }
}
