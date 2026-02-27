namespace ClaudeCodeWin.Models;

public enum ReviewRole { Reviewer, Driver, Judge, System }

public enum ReviewStatus { Idle, InProgress, Consensus, Disagreement, Escalated, Dismissed }

public class ReviewMessage
{
    public ReviewRole Role { get; set; }
    public string Text { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
