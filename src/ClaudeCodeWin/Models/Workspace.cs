namespace ClaudeCodeWin.Models;

public class Workspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<WorkspaceProject> Projects { get; set; } = [];
    public string? PrimaryProjectPath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastOpenedAt { get; set; } = DateTime.Now;

    public Workspace Clone() => new()
    {
        Id = Id, Name = Name, PrimaryProjectPath = PrimaryProjectPath,
        CreatedAt = CreatedAt, LastOpenedAt = LastOpenedAt,
        Projects = Projects.Select(p => new WorkspaceProject { Path = p.Path, Role = p.Role }).ToList()
    };
}

public class WorkspaceProject
{
    public string Path { get; set; } = "";
    public string? Role { get; set; }
}
