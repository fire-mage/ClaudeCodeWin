namespace ClaudeCodeWin.Models;

public class AppSettings
{
    public string? ClaudeExePath { get; set; }
    public string? WorkingDirectory { get; set; }
    public List<string> RecentFolders { get; set; } = [];

    // Window state
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public int? WindowState { get; set; } // 0=Normal, 2=Maximized

    // Session persistence per project
    public Dictionary<string, SavedSession> SavedSessions { get; set; } = [];

    // Auto-confirm ExitPlanMode (plan approval without user interaction)
    public bool AutoConfirmPlanMode { get; set; } = true;

    // Context Snapshot: auto-generate project context for Claude on session start
    public bool ContextSnapshotEnabled { get; set; } = true;

    // SSH key path for Claude's own SSH access
    public string? SshKeyPath { get; set; }

    // Known servers where Claude's SSH key is authorized
    public List<ServerInfo> Servers { get; set; } = [];

    // Update channel: "stable" (default) or "beta"
    public string UpdateChannel { get; set; } = "stable";
}
