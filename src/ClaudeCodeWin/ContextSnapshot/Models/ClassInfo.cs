namespace ClaudeCodeWin.ContextSnapshot.Models;

public class ClassInfo
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? BaseType { get; set; }
    public List<string> Interfaces { get; set; } = [];
    public List<string> Attributes { get; set; } = [];
    public string? RouteTemplate { get; set; }
    public List<SnapshotMethodInfo> Methods { get; set; } = [];
    public List<SnapshotPropertyInfo> Properties { get; set; } = [];
    public List<string> Dependencies { get; set; } = [];
    public string FilePath { get; set; } = "";
}
