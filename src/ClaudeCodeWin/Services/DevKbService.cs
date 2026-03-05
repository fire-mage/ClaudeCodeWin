using System.IO;
using System.Net.Http;
using System.Text.Json;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class DevKbService
{
    private const string ManifestUrl = "https://landing.qr4k.com/ccw-kb/manifest.json";

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeWin");

    private static readonly string CachePath = Path.Combine(CacheDir, "dev-kb-manifest.json");

    // FIX (WARNING #2): PooledConnectionLifetime forces periodic DNS re-resolution
    // in long-running desktop apps where DNS entries may change.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    // Fix: volatile for thread-safe reads — SyncAsync writes on threadpool, GetAllArticles reads on UI thread
    private volatile DevKbManifest? _manifest;

    /// <summary>
    /// Loads cached manifest immediately, then fetches latest from S3 in background.
    /// Call once at startup (fire-and-forget).
    /// </summary>
    public async Task SyncAsync()
    {
        // 1. Load local cache (instant, works offline)
        _manifest = LoadFromCache();

        // 2. Try fetching from S3
        try
        {
            var json = await Http.GetStringAsync(ManifestUrl).ConfigureAwait(false);
            var remote = JsonSerializer.Deserialize<DevKbManifest>(json, JsonDefaults.ReadOptions);
            if (remote?.Articles is not null)
            {
                _manifest = remote;
                SaveToCache(json);
            }
        }
        catch
        {
            // Network failure — use cached version silently
        }
    }

    public List<DevKbArticle> GetAllArticles() => _manifest?.Articles ?? [];

    public List<DevKbArticle> GetRequiredArticles() =>
        GetAllArticles().Where(a => a.IsRequired).ToList();

    /// <summary>
    /// Builds a section for system instruction injection containing all required articles.
    /// Returns empty string if no required articles exist.
    /// </summary>
    public string BuildRequiredArticlesSection()
    {
        var required = GetRequiredArticles();
        if (required.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("<developer-knowledge-base>");
        sb.AppendLine("The following articles are mandatory reading from the CCW development team.");
        sb.AppendLine("Follow these guidelines when working with the features described below.");

        foreach (var article in required)
        {
            sb.AppendLine();
            sb.AppendLine($"### {article.Title}");
            sb.AppendLine(article.ContentMd);
        }

        sb.AppendLine("</developer-knowledge-base>");
        return sb.ToString();
    }

    private static DevKbManifest? LoadFromCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var json = File.ReadAllText(CachePath);
            return JsonSerializer.Deserialize<DevKbManifest>(json, JsonDefaults.ReadOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveToCache(string json)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(CachePath, json);
        }
        catch
        {
            // Cache write failure is non-critical
        }
    }
}
