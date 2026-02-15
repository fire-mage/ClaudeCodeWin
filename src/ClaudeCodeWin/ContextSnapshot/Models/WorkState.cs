namespace ClaudeCodeWin.ContextSnapshot.Models;

public class WorkState
{
    public DateTime LastUpdated { get; set; }
    public string CurrentFocus { get; set; } = "";
    public List<string> PendingTasks { get; set; } = [];
    public List<RecentChange> RecentChanges { get; set; } = [];
    public List<string> KnownIssues { get; set; } = [];
    public string SessionNotes { get; set; } = "";
}

public class RecentChange
{
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Files { get; set; } = [];
}
