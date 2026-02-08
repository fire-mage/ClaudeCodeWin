namespace ClaudeCodeWin.Models;

public class TaskDefinition
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public string? HotKey { get; set; }
    public bool ConfirmBeforeRun { get; set; } = true;
}
