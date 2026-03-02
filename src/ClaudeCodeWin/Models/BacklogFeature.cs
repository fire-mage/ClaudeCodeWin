namespace ClaudeCodeWin.Models;

public enum FeatureStatus
{
    Planning, PlanningFailed, AwaitingUser,
    PlanReady, PlanApproved,
    Queued, InProgress, Done, Cancelled
}

public enum AwaitingUserReason { PlanningQuestion }

public class BacklogFeature
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string ProjectPath { get; set; } = "";
    public string RawIdea { get; set; } = "";
    public string? UserContext { get; set; }
    public string? Title { get; set; }
    public FeatureStatus Status { get; set; } = FeatureStatus.Planning;
    public string? PlannerSessionId { get; set; }
    public bool NeedsUserInput { get; set; }
    public string? PlannerQuestion { get; set; }
    public AwaitingUserReason? AwaitingReason { get; set; }
    public int Priority { get; set; } = 100;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<BacklogPhase> Phases { get; set; } = [];

    public string? RejectionReason { get; set; }

    // Plan review fields
    public string? PlanReviewVerdict { get; set; }
    public string? PlanReviewComments { get; set; }
    public List<string> PlanReviewSuggestions { get; set; } = [];

    // Session history (dev/review transcripts)
    public List<string> SessionHistoryPaths { get; set; } = [];

    // Error info (when feature returns to backlog after failure)
    public string? ErrorSummary { get; set; }
    public string? ErrorDetails { get; set; }

    // Review dismiss tracking
    public bool ReviewDismissed { get; set; }

    // Archive
    public DateTime? ArchivedAt { get; set; }
}
