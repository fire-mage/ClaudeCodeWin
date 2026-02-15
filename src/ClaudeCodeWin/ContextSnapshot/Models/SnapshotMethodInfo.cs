namespace ClaudeCodeWin.ContextSnapshot.Models;

public class SnapshotMethodInfo
{
    public string Name { get; set; } = "";
    public string ReturnType { get; set; } = "";
    public string Parameters { get; set; } = "";
    public List<string> Attributes { get; set; } = [];
    public string? HttpMethod { get; set; }
    public string? RoutePath { get; set; }
    public bool IsAsync { get; set; }
}
