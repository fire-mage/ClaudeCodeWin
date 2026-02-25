namespace ClaudeCodeWin.Models;

public enum BackgroundTaskStatus { Running, Completed, Failed }

public record BackgroundTaskInfo(
    string ToolUseId,
    string Description,
    DateTime StartTime
)
{
    public string? AgentId { get; set; }
    public BackgroundTaskStatus Status { get; set; } = BackgroundTaskStatus.Running;
    public DateTime? EndTime { get; set; }
    public string? Output { get; set; }
}
