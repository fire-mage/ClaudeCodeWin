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

    // Current usage data
    public double SessionUtilization { get; private set; }
    public DateTime? SessionResetsAt { get; private set; }
    public double WeeklyUtilization { get; private set; }
    public DateTime? WeeklyResetsAt { get; private set; }
    public bool IsLoaded { get; private set; }
    public bool IsOnline { get; private set; } = true;
    public bool IsRateLimited { get; private set; }

    // Events
    public event Action? OnUsageUpdated;
    public event Action<bool>? OnRateLimitChanged; // true = rate limited, false = cleared

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
            if (_accessToken is null) return;
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
                // Token might be expired — try re-reading
                if ((int)response.StatusCode == 401)
                {
                    _accessToken = ReadAccessToken();
                }
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

            IsLoaded = true;

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
            {
                IsOnline = true;
                OnUsageUpdated?.Invoke();
            }
            else
            {
                OnUsageUpdated?.Invoke();
            }
        }
        catch (HttpRequestException)
        {
            if (IsOnline)
            {
                IsOnline = false;
                OnUsageUpdated?.Invoke();
            }
        }
        catch (TaskCanceledException)
        {
            // Timeout — also a network issue
            if (IsOnline)
            {
                IsOnline = false;
                OnUsageUpdated?.Invoke();
            }
        }
        catch
        {
            // Other errors — silently ignore
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
}
