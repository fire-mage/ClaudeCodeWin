namespace ClaudeCodeWin.Models;

public enum PhaseStatus { Pending, InProgress, InReview, Done, Failed }

public class BacklogPhase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public int Order { get; set; }
    public string Title { get; set; } = "";
    public string Plan { get; set; } = "";
    public string? AcceptanceCriteria { get; set; }
    public PhaseStatus Status { get; set; } = PhaseStatus.Pending;
    public string? DevSessionId { get; set; }
    public string? Summary { get; set; }
    public List<string> ChangedFiles { get; set; } = [];
    public string? CommitHash { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
