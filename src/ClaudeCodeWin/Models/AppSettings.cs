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
}
