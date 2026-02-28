namespace ClaudeCodeWin.Models;

public enum TeamRole { Planner, Developer, Reviewer }
public enum SessionHealth { Healthy, Stalled, Error, RateLimited, Stopped }

public class TeamSessionInfo
{
    public TeamRole Role { get; set; }
    public string? SessionId { get; set; }
    public string? FeatureId { get; set; }
    public string? PhaseId { get; set; }
    public string ProjectPath { get; set; } = "";
    public SessionHealth Health { get; set; } = SessionHealth.Healthy;
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public string? StatusText { get; set; }
}
