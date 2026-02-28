namespace ClaudeCodeWin.Models;

public enum FeatureStatus { Raw, Planning, AwaitingUser, Planned, InProgress, Done, Cancelled }

public class BacklogFeature
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string ProjectPath { get; set; } = "";
    public string RawIdea { get; set; } = "";
    public string? Title { get; set; }
    public FeatureStatus Status { get; set; } = FeatureStatus.Raw;
    public string? PlannerSessionId { get; set; }
    public bool NeedsUserInput { get; set; }
    public string? PlannerQuestion { get; set; }
    public int Priority { get; set; } = 100;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<BacklogPhase> Phases { get; set; } = [];
}
