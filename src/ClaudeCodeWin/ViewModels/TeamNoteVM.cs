using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public class TeamNoteVM
{
    public string NoteId { get; }
    public string RoleIcon { get; }
    public string RoleLabel { get; }
    public string FeatureReference { get; }
    public string Message { get; }

    private readonly DateTime _timestamp;

    /// <summary>
    /// Computed each time the getter is called so relative times stay fresh.
    /// Note: WPF reads this once on binding and caches the value since TeamNoteVM
    /// doesn't implement INotifyPropertyChanged. Timestamps refresh when the panel
    /// is toggled (VMs are re-created). While the panel stays open, values go stale.
    /// </summary>
    public string TimestampText => FormatTimestamp(_timestamp);

    public TeamNoteVM(TeamNote note)
    {
        NoteId = note.Id;
        Message = note.Message;
        FeatureReference = note.FeatureTitle ?? "";
        _timestamp = note.Timestamp;

        (RoleIcon, RoleLabel) = note.SourceRole.ToLowerInvariant() switch
        {
            "planner"  => ("\U0001F4CB", "Planner"),
            "developer" or "dev" => ("\U0001F528", "Developer"),
            "reviewer" or "review" => ("\U0001F441", "Reviewer"),
            "plan-reviewer" or "planreviewer" => ("\U0001F4CB", "Plan Reviewer"),
            "manager"  => ("\U0001F451", "Manager"),
            "orchestrator" => ("\u2699", "Orchestrator"),
            _ => ("\U0001F4AC", note.SourceRole)
        };
    }

    private static string FormatTimestamp(DateTime timestamp)
    {
        var elapsed = DateTime.Now - timestamp;

        if (elapsed.TotalSeconds < 60)
            return "just now";
        if (elapsed.TotalMinutes < 60)
            return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed.TotalHours < 24)
            return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 7)
            return $"{(int)elapsed.TotalDays}d ago";

        return timestamp.ToString("MMM d, HH:mm");
    }
}
