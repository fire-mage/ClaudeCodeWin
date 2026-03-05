using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class McpRegistryService
{
    private const string BaseUrl = "https://registry.modelcontextprotocol.io/v0/servers";
    private const int PageSize = 100;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin", "marketplace");

    private static readonly string CachePath = Path.Combine(CacheDir, "registry-cache.json");

    // FIX (WARNING #2): PooledConnectionLifetime forces periodic DNS re-resolution.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    // Allowed characters for shell slugs: alphanumeric, hyphen, underscore, dot
    private static readonly Regex SafeSlugRegex = new(@"^[\w.\-]+$", RegexOptions.Compiled);

    /// <summary>
    /// Returns cached servers if cache is fresh (&lt;24h), otherwise fetches first page from API.
    /// <paramref name="fromCache"/> is true when the returned data came from local cache.
    /// </summary>
    public async Task<(List<McpRegistryServer> Servers, bool FromCache)> GetCachedOrFetchAsync(CancellationToken ct = default)
    {
        var cache = LoadCache();
        if (cache is not null && (DateTime.UtcNow - cache.FetchedAt) < CacheTtl)
            return (DeduplicateByName(cache.Servers.Where(s => s.Packages.Count > 0).ToList()), true);

        var servers = await FetchFirstPageAsync(ct);
        SaveCache(new RegistryCache { FetchedAt = DateTime.UtcNow, Servers = servers });
        return (servers, false);
    }

    public async Task<List<McpRegistryServer>> RefreshCacheAsync(CancellationToken ct = default)
    {
        var servers = await FetchFirstPageAsync(ct);
        SaveCache(new RegistryCache { FetchedAt = DateTime.UtcNow, Servers = servers });
        return servers;
    }

    public async Task<List<McpRegistryServer>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}?limit={PageSize}&search={Uri.EscapeDataString(query)}";
        return await FetchFromUrlAsync(url, ct);
    }

    public static string GenerateInstallCommand(McpRegistryServer server)
    {
        var slug = SanitizeSlug(server.Name.Replace('/', '-'));

        // Package-based servers (npm, pypi, docker) — preferred over remote
        if (server.Packages.Count > 0)
        {
            var pkg = server.Packages[0];
            var envFlags = "";
            if (pkg.EnvironmentVariables is { Count: > 0 })
            {
                var envParts = pkg.EnvironmentVariables
                    .Select(e => $"--env {QuoteArg(e.Name)}=<value>")
                    .ToList();
                envFlags = " " + string.Join(" ", envParts);
            }

            var identifier = QuoteArg(pkg.Identifier);
            return pkg.RegistryType.ToLowerInvariant() switch
            {
                "npm" => $"claude mcp add --transport stdio{envFlags} {slug} -- npx -y {identifier}",
                "pypi" => $"claude mcp add --transport stdio{envFlags} {slug} -- uvx {identifier}",
                "oci" => $"claude mcp add --transport stdio{envFlags} {slug} -- docker run -i {identifier}",
                _ => $"claude mcp add --transport stdio{envFlags} {slug} -- {identifier}"
            };
        }

        return $"# No install command available for {server.Name}";
    }

    public static string GetKbTag(McpRegistryServer server)
        => $"mcp-{SanitizeSlug(server.Name.Replace('/', '-'))}";

    private async Task<List<McpRegistryServer>> FetchFirstPageAsync(CancellationToken ct)
    {
        var url = $"{BaseUrl}?limit={PageSize}";
        return await FetchFromUrlAsync(url, ct);
    }

    private static async Task<List<McpRegistryServer>> FetchFromUrlAsync(string url, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(url, ct);
        var response = JsonSerializer.Deserialize<RegistryApiResponse>(json, JsonDefaults.ReadOptions);
        if (response?.Servers is null)
            return [];

        var servers = response.Servers
            .Where(e => e.Server is not null)
            .Select(e => e.Server!)
            .Where(s => s.Packages.Count > 0) // Exclude remote-only servers (OAuth not supported by CLI)
            .ToList();

        return DeduplicateByName(servers);
    }

    /// <summary>
    /// API returns all versions of each server — deduplicate by Name, keep latest version.
    /// Also applied to cached data in case cache was written before deduplication was added.
    /// </summary>
    private static List<McpRegistryServer> DeduplicateByName(List<McpRegistryServer> servers)
    {
        return servers
            .GroupBy(s => s.Name)
            .Select(g => g.OrderBy(s => ParseVersion(s.Version)).Last())
            .ToList();
    }

    private static (int, int, int) ParseVersion(string v)
    {
        if (string.IsNullOrEmpty(v)) return (0, 0, 0);
        var parts = v.TrimStart('v').Split('.', 3);
        int.TryParse(parts.ElementAtOrDefault(0), out var major);
        int.TryParse(parts.ElementAtOrDefault(1), out var minor);
        int.TryParse(parts.ElementAtOrDefault(2)?.Split('-')[0], out var patch);
        return (major, minor, patch);
    }

    private static string SanitizeSlug(string name)
    {
        if (SafeSlugRegex.IsMatch(name))
            return name;
        // Strip anything that's not a safe character
        var sanitized = Regex.Replace(name, @"[^\w.\-]", "");
        return string.IsNullOrEmpty(sanitized) ? "mcp-server" : sanitized;
    }

    private static string QuoteArg(string value)
    {
        // Strip all control characters (newlines, null bytes, ANSI escapes, etc.)
        value = new string(value.Where(c => !char.IsControl(c)).ToArray());
        // POSIX single-quote escaping: wrap in single quotes, escape inner ' as '\''
        // Safe for bash (Claude CLI's Bash tool on Windows uses bash)
        return "'" + value.Replace("'", @"'\''") + "'";
    }

    private void SaveCache(RegistryCache cache)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var json = JsonSerializer.Serialize(cache, JsonDefaults.Options);
            File.WriteAllText(CachePath, json);
        }
        catch { /* cache write failure is non-critical */ }
    }

    private static RegistryCache? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var json = File.ReadAllText(CachePath);
            return JsonSerializer.Deserialize<RegistryCache>(json, JsonDefaults.ReadOptions);
        }
        catch { return null; }
    }

    // JSON response models (internal)

    private class RegistryApiResponse
    {
        public List<RegistryEntry> Servers { get; set; } = [];
    }

    private class RegistryEntry
    {
        public McpRegistryServer? Server { get; set; }
    }

    private class RegistryCache
    {
        public DateTime FetchedAt { get; set; }
        public List<McpRegistryServer> Servers { get; set; } = [];
    }
}
