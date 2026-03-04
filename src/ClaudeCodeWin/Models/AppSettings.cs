namespace ClaudeCodeWin.Models;

public class AppSettings
{
    // Schema version for one-time migrations (bump when adding new migrations)
    public int SettingsVersion { get; set; }

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

    // Master password for SSH servers that don't accept SSH key auth
    // Legacy plaintext field — auto-migrated to SshMasterPasswordProtected on load
    public string? SshMasterPassword { get; set; }

    // DPAPI-encrypted SSH master password (base64 blob)
    public string? SshMasterPasswordProtected { get; set; }

    // Known servers where Claude's SSH key is authorized
    public List<ServerInfo> Servers { get; set; } = [];

    // Update channel: "stable" (default) or "beta"
    public string UpdateChannel { get; set; } = "stable";

    // Diagnostic logging: log raw stream-json to %LocalAppData%/ClaudeCodeWin/logs/
    public bool DiagnosticLoggingEnabled { get; set; }

    // Projects where user dismissed task suggestion popup
    public List<string> TaskSuggestionDismissedProjects { get; set; } = [];

    // Versions that failed to start after update (blacklisted from re-offering)
    public List<string> FailedUpdateVersions { get; set; } = [];

    // CLI versions that failed after update (blacklisted from re-offering)
    public List<string> FailedCliVersions { get; set; } = [];

    // CCW Feature Request: remember user email
    public string? UserEmail { get; set; }

    // CCW Activation Code
    public string? ActivationCode { get; set; }
    public List<string> ActivatedFeatures { get; set; } = [];

    // Tab persistence: restore open project tabs across sessions
    public List<string>? OpenTabPaths { get; set; }
    public string? ActiveTabPath { get; set; }

    // Left panel width (vertical project tabs, GridSplitter position)
    public double? ProjectTabPanelWidth { get; set; }

    // Left tab panel compact mode (narrow with rotated labels)
    public bool TabPanelCompact { get; set; }

    // Development Team: auto-approve plans (skip manual plan approval gate)
    public bool AutoApprovePlans { get; set; }

    // Development Team: auto-review after task completion
    public bool ReviewerEnabled { get; set; } = true;
    public int ReviewAutoRetries { get; set; } = 11;
    public int ReviewTimeoutSeconds { get; set; } = 660;

    // Development Team: dev stall detection (nudge → kill → retry)
    public int DevNudgeSeconds { get; set; } = 360;
    public int DevTimeoutSeconds { get; set; } = 660;
    public int DevStallMaxRetries { get; set; } = 2;

    // Legacy migration: map old ExtremeCodeEnabled to ReviewerEnabled
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ExtremeCodeEnabled { get => ReviewerEnabled; set => ReviewerEnabled = value; }

    // External service API keys with expiry tracking
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];
}

public class ApiKeyEntry
{
    public string ServiceId { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string? KeyProtected { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int WarningDays { get; set; } = 14;

    public ApiKeyEntry Clone() => new()
    {
        ServiceId = ServiceId, ServiceName = ServiceName,
        KeyProtected = KeyProtected, ExpiresAt = ExpiresAt,
        WarningDays = WarningDays
    };

    /// <summary>Returns (days until expiry, is expired, is in warning zone). Days=0 if no expiry set.</summary>
    public (int Days, bool IsExpired, bool IsWarning) GetExpiryStatus()
    {
        if (!ExpiresAt.HasValue) return (0, false, false);
        var days = (ExpiresAt.Value.Date - DateTime.Today).Days;
        var isExpired = days < 0;
        // WarningDays <= 0 means "don't warn" (only report expired)
        var isWarning = !isExpired && WarningDays > 0 && days <= WarningDays;
        return (days, isExpired, isWarning);
    }
}
