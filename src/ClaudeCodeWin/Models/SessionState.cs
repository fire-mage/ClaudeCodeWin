namespace ClaudeCodeWin.Models;

public class SessionState
{
    public string? SessionId { get; set; }
    public string? Model { get; set; }
    public double CostUsd { get; set; }
    public bool IsProcessing { get; set; }
    public string? WorkingDirectory { get; set; }
}
