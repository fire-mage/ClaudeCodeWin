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

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

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
            return (cache.Servers, true);

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
        var slug = SanitizeSlug(server.DisplayName);

        // Remote servers (HTTP/SSE)
        if (server.Remotes.Count > 0)
        {
            var remote = server.Remotes[0];
            var transport = remote.Type switch
            {
                "streamable-http" => "http",
                "sse" => "sse",
                _ => "http"
            };
            var url = QuoteArg(remote.Url);
            return $"claude mcp add --transport {transport} {slug} {url}";
        }

        // Package-based servers (npm, pypi, docker)
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

        return response.Servers
            .Where(e => e.Server is not null)
            .Select(e => e.Server!)
            .ToList();
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
        // Windows cmd.exe: use double quotes; escape inner double quotes with backslash
        if (value.Contains(' ') || value.Contains(';') || value.Contains('&') ||
            value.Contains('|') || value.Contains('$') || value.Contains('`') ||
            value.Contains('\'') || value.Contains('"') || value.Contains('%'))
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        return value;
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
