namespace ClaudeCodeWin.Models;

/// <summary>
/// Snapshot of a single active session's health, emitted by TeamOrchestratorService every 10s.
/// </summary>
public class SessionHealthInfo
{
    public SessionHealthInfo(string role, SessionHealth health, string detail, string elapsed,
        int idleSeconds = 0, int reviewRound = 0, int maxReviewRounds = 0, string phaseName = "")
    {
        Role = role;
        Health = health;
        Detail = detail;
        Elapsed = elapsed;
        IdleSeconds = idleSeconds;
        ReviewRound = reviewRound;
        MaxReviewRounds = maxReviewRounds;
        PhaseName = phaseName;
    }

    public string Role { get; }
    public SessionHealth Health { get; }
    public string Detail { get; }
    public string Elapsed { get; }
    public int IdleSeconds { get; }
    public int ReviewRound { get; }
    public int MaxReviewRounds { get; }
    public string PhaseName { get; }
    public bool HasIssue => Health != SessionHealth.Healthy;

    public string HealthIcon => Health switch
    {
        SessionHealth.Healthy => "\u25CF",      // ●
        SessionHealth.Stalled => "\u26A0",      // ⚠
        SessionHealth.Error => "\u2715",         // ✕
        SessionHealth.RateLimited => "\u23F1",   // ⏱
        _ => "\u25CB"                            // ○
    };
}
