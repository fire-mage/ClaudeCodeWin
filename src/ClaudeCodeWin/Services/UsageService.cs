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

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin", "usage-cache.json");

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    // Default poll interval: 5 minutes. Increased on 429 and persisted to settings.
    private const int DefaultPollSeconds = 300;
    private const int MaxPollSeconds = 1800; // 30 minutes cap
    private const int CacheMaxMinutes = 10;

    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _countdownTimer;
    private string? _accessToken;
    private int _consecutiveFailures;
    private bool _rawLogged;

    // External dependencies for persisting poll interval
    private Models.AppSettings? _appSettings;
    private SettingsService? _settingsService;

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
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(DefaultPollSeconds) };
        _pollTimer.Tick += async (_, _) => await FetchUsageAsync();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => OnUsageUpdated?.Invoke();
    }

    /// <summary>
    /// Configure settings persistence so poll interval changes survive restarts.
    /// Call before Start().
    /// </summary>
    public void Configure(Models.AppSettings settings, SettingsService settingsService)
    {
        _appSettings = settings;
        _settingsService = settingsService;

        // Restore persisted poll interval (from previous 429 backoffs)
        if (settings.UsagePollIntervalSeconds > 0)
            _pollTimer.Interval = TimeSpan.FromSeconds(
                Math.Clamp(settings.UsagePollIntervalSeconds, DefaultPollSeconds, MaxPollSeconds));
    }

    public void Start()
    {
        _accessToken = ReadAccessToken();

        // Load cached usage data so status bar shows immediately, even if API is slow or returns 429
        LoadCache();

        _pollTimer.Start();
        _countdownTimer.Start();

        if (_accessToken is not null)
            _ = FetchUsageAsync();
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
                return;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_accessToken}");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("User-Agent", "ClaudeCodeWin/1.0");

            var response = await Http.SendAsync(request);

            // Any HTTP response means server is reachable — clear offline state
            _consecutiveFailures = 0;
            if (!IsOnline) { IsOnline = true; OnUsageUpdated?.Invoke(); }

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 401)
                {
                    _accessToken = ReadAccessToken();
                }
                else if ((int)response.StatusCode == 429)
                {
                    // Exponential backoff: double interval, cap at 30 minutes, persist to settings
                    var current = (int)_pollTimer.Interval.TotalSeconds;
                    var next = Math.Min(current * 2, MaxPollSeconds);
                    SetPollInterval(next);
                    DiagnosticLogger.Log("USAGE_RATE_LIMITED", $"429 — backoff to {next}s");
                }

                // Keep countdowns alive from cached data
                if (IsLoaded)
                    OnUsageUpdated?.Invoke();
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

            if (root.TryGetProperty("daily", out var daily))
            {
                DailyUtilization = daily.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0;
                DailyResetsAt = daily.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(r.GetString()!).LocalDateTime
                    : null;
            }

            if (root.TryGetProperty("sonnet", out var sonnet))
            {
                SonnetUtilization = sonnet.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0;
                SonnetResetsAt = sonnet.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(r.GetString()!).LocalDateTime
                    : null;
            }

            if (!_rawLogged)
            {
                DiagnosticLogger.Log("USAGE_RAW", json);
                _rawLogged = true;
            }

            IsLoaded = true;
            SaveCache();

            // Successful fetch — reset backoff to default if it was increased by 429
            if (_pollTimer.Interval.TotalSeconds > DefaultPollSeconds)
            {
                _pollTimer.Interval = TimeSpan.FromSeconds(DefaultPollSeconds);
                // Persist 0 = "use code default" so future DefaultPollSeconds changes take effect
                if (_appSettings is not null && _settingsService is not null)
                {
                    _appSettings.UsagePollIntervalSeconds = 0;
                    _settingsService.Save(_appSettings);
                }
            }

            // Rate limit detection
            var wasRateLimited = IsRateLimited;
            IsRateLimited = SessionUtilization >= 100;

            if (IsRateLimited != wasRateLimited)
                OnRateLimitChanged?.Invoke(IsRateLimited);

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
        catch (Exception ex)
        {
            DiagnosticLogger.Log("USAGE_PARSE_ERROR", ex.Message);
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
    /// </summary>
    public void SetRateLimitedExternally()
    {
        if (IsRateLimited) return;
        IsRateLimited = true;
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

    /// <summary>
    /// Set poll interval and persist to settings so it survives app restarts.
    /// </summary>
    private void SetPollInterval(int seconds)
    {
        _pollTimer.Interval = TimeSpan.FromSeconds(seconds);

        if (_appSettings is not null && _settingsService is not null)
        {
            _appSettings.UsagePollIntervalSeconds = seconds;
            _settingsService.Save(_appSettings);
        }
    }

    /// <summary>
    /// Persists current usage data to disk. All timestamps stored as UTC.
    /// </summary>
    private void SaveCache()
    {
        try
        {
            var cache = new
            {
                sessionUtilization = SessionUtilization,
                sessionResetsAt = SessionResetsAt?.ToUniversalTime().ToString("o"),
                weeklyUtilization = WeeklyUtilization,
                weeklyResetsAt = WeeklyResetsAt?.ToUniversalTime().ToString("o"),
                dailyUtilization = DailyUtilization,
                dailyResetsAt = DailyResetsAt?.ToUniversalTime().ToString("o"),
                sonnetUtilization = SonnetUtilization,
                sonnetResetsAt = SonnetResetsAt?.ToUniversalTime().ToString("o"),
                isRateLimited = IsRateLimited,
                savedAt = DateTime.UtcNow.ToString("o")
            };
            var json = JsonSerializer.Serialize(cache);
            var dir = Path.GetDirectoryName(CachePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex) { DiagnosticLogger.Log("USAGE_CACHE_SAVE_ERROR", ex.Message); }
    }

    /// <summary>
    /// Loads cached usage data from disk. Cache is valid for 10 minutes max.
    /// </summary>
    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var json = File.ReadAllText(CachePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("savedAt", out var savedAt) || savedAt.ValueKind != JsonValueKind.String) return;
            var saved = DateTime.Parse(savedAt.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
            if (DateTime.UtcNow - saved > TimeSpan.FromMinutes(CacheMaxMinutes)) return;

            if (root.TryGetProperty("sessionUtilization", out var su))
                SessionUtilization = su.GetDouble();
            if (root.TryGetProperty("sessionResetsAt", out var sr) && sr.ValueKind == JsonValueKind.String)
                SessionResetsAt = DateTime.Parse(sr.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime();
            if (root.TryGetProperty("weeklyUtilization", out var wu))
                WeeklyUtilization = wu.GetDouble();
            if (root.TryGetProperty("weeklyResetsAt", out var wr) && wr.ValueKind == JsonValueKind.String)
                WeeklyResetsAt = DateTime.Parse(wr.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime();
            if (root.TryGetProperty("dailyUtilization", out var du))
                DailyUtilization = du.GetDouble();
            if (root.TryGetProperty("dailyResetsAt", out var dr) && dr.ValueKind == JsonValueKind.String)
                DailyResetsAt = DateTime.Parse(dr.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime();
            if (root.TryGetProperty("sonnetUtilization", out var sonu))
                SonnetUtilization = sonu.GetDouble();
            if (root.TryGetProperty("sonnetResetsAt", out var sonr) && sonr.ValueKind == JsonValueKind.String)
                SonnetResetsAt = DateTime.Parse(sonr.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime();

            // Restore rate limit state (only if reset time is still in the future)
            if (root.TryGetProperty("isRateLimited", out var rl) && rl.GetBoolean()
                && SessionResetsAt.HasValue && SessionResetsAt.Value > DateTime.Now)
            {
                IsRateLimited = true;
                OnRateLimitChanged?.Invoke(true);
            }

            IsLoaded = true;
            OnUsageUpdated?.Invoke();
            DiagnosticLogger.Log("USAGE_CACHE", "Loaded cached usage data");
        }
        catch (Exception ex) { DiagnosticLogger.Log("USAGE_CACHE_LOAD_ERROR", ex.Message); }
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
        catch (Exception ex) { DiagnosticLogger.Log("CREDENTIALS_READ_ERROR", ex.Message); }
        return null;
    }

}
