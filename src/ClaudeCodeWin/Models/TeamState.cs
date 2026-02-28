namespace ClaudeCodeWin.Models;

public enum TeamMode { Running, SoftPause, HardPause }

public class TeamState
{
    public TeamMode Mode { get; set; } = TeamMode.Running;
    public string? PauseReason { get; set; }
    public DateTime? PausedAt { get; set; }
    public List<TeamSessionInfo> ActiveSessions { get; set; } = [];
}
