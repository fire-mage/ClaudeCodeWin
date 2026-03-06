using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace ClaudeCodeWin.Services;

public class UsageService
{
    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _countdownTimer;
    private string? _accessToken;
    private int _consecutiveFailures;

    // Current usage data
    public double SessionUtilization { get; private set; }
    public DateTime? SessionResetsAt { get; private set; }
    public double WeeklyUtilization { get; private set; }
    public DateTime? WeeklyResetsAt { get; private set; }
    public double DailyUtilization { get; private set; }
    public DateTime? DailyResetsAt { get; private set; }
    public double SonnetUtilization { get; private set; }
    public DateTime? SonnetResetsAt { get; private set; }
    public bool IsLoaded { get; private set; }
    public bool IsOnline { get; private set; } = true;
    public bool IsRateLimited { get; private set; }

    // Events
    public event Action? OnUsageUpdated;
    public event Action<bool>? OnRateLimitChanged; // true = rate limited, false = cleared
    public event Action? OnFetchSuccess; // fires only after successful API fetch (not countdown ticks)

    public UsageService()
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _pollTimer.Tick += async (_, _) => await FetchUsageAsync();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => OnUsageUpdated?.Invoke();
    }

    public void Start()
    {
        _accessToken = ReadAccessToken();
        if (_accessToken is null) return;

        _ = FetchUsageAsync();
        _pollTimer.Start();
        _countdownTimer.Start();
    }

    public void Stop()
    {
        _pollTimer.Stop();
        _countdownTimer.Stop();
    }

    public async Task FetchUsageAsync()
    {
        if (_accessToken is null)
        {
            _accessToken = ReadAccessToken();
            if (_accessToken is null)
            {
                return;
            }
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_accessToken}");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("User-Agent", "ClaudeCodeWin/1.0");

            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 401)
                    _accessToken = ReadAccessToken();
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("five_hour", out var fiveHour))
            {
                SessionUtilization = fiveHour.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0;
                SessionResetsAt = fiveHour.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(r.GetString()!).LocalDateTime
                    : null;
            }

            if (root.TryGetProperty("seven_day", out var sevenDay))
            {
                WeeklyUtilization = sevenDay.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0;
                WeeklyResetsAt = sevenDay.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(r.GetString()!).LocalDateTime
                    : null;
            }

            // Parse daily limit if present
            if (root.TryGetProperty("daily", out var daily))
            {
                DailyUtilization = daily.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0;
                DailyResetsAt = daily.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(r.GetString()!).LocalDateTime
                    : null;
            }

            // Parse sonnet-specific limit if present
            if (root.TryGetProperty("sonnet", out var sonnet))
            {
                SonnetUtilization = sonnet.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0;
                SonnetResetsAt = sonnet.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(r.GetString()!).LocalDateTime
                    : null;
            }

            // Log raw API response on first load only (avoid spamming logs every minute)
            if (!IsLoaded)
                DiagnosticLogger.Log("USAGE_RAW", json);

            IsLoaded = true;
            _consecutiveFailures = 0;

            // Rate limit detection
            var wasRateLimited = IsRateLimited;
            IsRateLimited = SessionUtilization >= 100;

            if (IsRateLimited != wasRateLimited)
            {
                // Switch poll interval: 15s during rate limit, 1min normally
                _pollTimer.Interval = IsRateLimited
                    ? TimeSpan.FromSeconds(15)
                    : TimeSpan.FromMinutes(1);
                OnRateLimitChanged?.Invoke(IsRateLimited);
            }

            if (!IsOnline)
                IsOnline = true;

            OnUsageUpdated?.Invoke();
            OnFetchSuccess?.Invoke();
        }
        catch (HttpRequestException)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= 3 && IsOnline)
            {
                IsOnline = false;
                OnUsageUpdated?.Invoke();
            }
        }
        catch (TaskCanceledException)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= 3 && IsOnline)
            {
                IsOnline = false;
                OnUsageUpdated?.Invoke();
            }
        }
        catch
        {
            // Other parse/unexpected errors — silently ignore
        }
    }

    public string GetSessionCountdown()
    {
        if (SessionResetsAt is null) return "";
        var remaining = SessionResetsAt.Value - DateTime.Now;
        if (remaining.TotalSeconds <= 0) return "resetting...";
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes:D2}m"
            : $"{remaining.Minutes}m {remaining.Seconds:D2}s";
    }

    public string GetWeeklyCountdown()
    {
        if (WeeklyResetsAt is null) return "";
        var remaining = WeeklyResetsAt.Value - DateTime.Now;
        if (remaining.TotalSeconds <= 0) return "resetting...";
        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes:D2}m"
            : $"{remaining.Minutes}m {remaining.Seconds:D2}s";
    }

    /// <summary>
    /// Force rate-limited state from external signal (e.g., CLI stderr).
    /// Triggers faster polling to detect when limit clears.
    /// </summary>
    public void SetRateLimitedExternally()
    {
        if (IsRateLimited) return;
        IsRateLimited = true;
        _pollTimer.Interval = TimeSpan.FromSeconds(15);
        OnRateLimitChanged?.Invoke(true);
        _ = FetchUsageAsync(); // immediate refresh
    }

    public string GetDailyCountdown()
    {
        if (DailyResetsAt is null) return "";
        var remaining = DailyResetsAt.Value - DateTime.Now;
        if (remaining.TotalSeconds <= 0) return "resetting...";
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes:D2}m"
            : $"{remaining.Minutes}m {remaining.Seconds:D2}s";
    }

    public string GetSonnetCountdown()
    {
        if (SonnetResetsAt is null) return "";
        var remaining = SonnetResetsAt.Value - DateTime.Now;
        if (remaining.TotalSeconds <= 0) return "resetting...";
        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes:D2}m"
            : $"{remaining.Minutes}m {remaining.Seconds:D2}s";
    }

    private static string? ReadAccessToken()
    {
        try
        {
            if (!File.Exists(CredentialsPath)) return null;
            var json = File.ReadAllText(CredentialsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.TryGetProperty("accessToken", out var token))
            {
                return token.GetString();
            }
        }
        catch { }
        return null;
    }
    /// <summary>Load cached usage from settings so the panel shows immediately on startup.</summary>
    public void LoadCachedUsage(Models.AppSettings settings)
    {
        if (settings.CachedSessionUtilization > 0 || settings.CachedWeeklyUtilization > 0)
        {
            SessionUtilization = settings.CachedSessionUtilization;
            WeeklyUtilization = settings.CachedWeeklyUtilization;
            SessionResetsAt = settings.CachedSessionResetsAt is not null
                ? DateTime.Parse(settings.CachedSessionResetsAt)
                : null;
            WeeklyResetsAt = settings.CachedWeeklyResetsAt is not null
                ? DateTime.Parse(settings.CachedWeeklyResetsAt)
                : null;
            IsLoaded = true;
            OnUsageUpdated?.Invoke();
        }
    }

    /// <summary>Save current usage to settings for next startup.</summary>
    public void SaveCachedUsage(Models.AppSettings settings)
    {
        if (!IsLoaded) return;
        settings.CachedSessionUtilization = SessionUtilization;
        settings.CachedWeeklyUtilization = WeeklyUtilization;
        settings.CachedSessionResetsAt = SessionResetsAt?.ToString("o");
        settings.CachedWeeklyResetsAt = WeeklyResetsAt?.ToString("o");
    }
}
