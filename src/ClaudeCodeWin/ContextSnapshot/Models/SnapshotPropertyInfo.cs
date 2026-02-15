namespace ClaudeCodeWin.ContextSnapshot.Models;

public class SnapshotPropertyInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsNavigation { get; set; }
    public bool IsCollection { get; set; }
}
