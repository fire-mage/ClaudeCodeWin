using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class MarketplaceService
{
    private static readonly string CustomPluginsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin", "marketplace");

    // FIX (WARNING #2): PooledConnectionLifetime forces periodic DNS re-resolution.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    public List<MarketplacePlugin> GetAllPlugins()
    {
        var plugins = new List<MarketplacePlugin>(GetBuiltInPlugins());
        plugins.AddRange(LoadCustomPlugins());
        return plugins;
    }

    public List<MarketplacePlugin> GetBuiltInPlugins() =>
    [
        new()
        {
            Id = "code-review-checklist",
            Name = "Code Review Checklist",
            Description = "Systematic checklist for thorough code reviews covering security, performance, and maintainability.",
            Author = "ClaudeCodeWin",
            Tags = ["review", "quality", "security"],
            IsBuiltIn = true,
            Content = BuiltInContent.CodeReviewChecklist
        },
        new()
        {
            Id = "git-workflow",
            Name = "Git Workflow & Conventions",
            Description = "Best practices for git branching, conventional commits, and collaborative workflows.",
            Author = "ClaudeCodeWin",
            Tags = ["git", "workflow", "conventions"],
            IsBuiltIn = true,
            Content = BuiltInContent.GitWorkflow
        },
        new()
        {
            Id = "api-security",
            Name = "API Security Fundamentals",
            Description = "OWASP top-10 vulnerabilities, input validation, authentication, and authorization patterns.",
            Author = "ClaudeCodeWin",
            Tags = ["security", "api", "owasp"],
            IsBuiltIn = true,
            Content = BuiltInContent.ApiSecurity
        },
        new()
        {
            Id = "performance-profiling",
            Name = "Performance Profiling Guide",
            Description = "How to identify bottlenecks, profile applications, and optimize for speed and memory.",
            Author = "ClaudeCodeWin",
            Tags = ["performance", "profiling", "optimization"],
            IsBuiltIn = true,
            Content = BuiltInContent.PerformanceProfiling
        },
        new()
        {
            Id = "testing-strategies",
            Name = "Testing Strategies",
            Description = "TDD workflow, unit/integration/e2e testing approaches, mocking, and test design patterns.",
            Author = "ClaudeCodeWin",
            Tags = ["testing", "tdd", "quality"],
            IsBuiltIn = true,
            Content = BuiltInContent.TestingStrategies
        }
    ];

    public List<MarketplacePlugin> LoadCustomPlugins()
    {
        if (!Directory.Exists(CustomPluginsDir))
            return [];

        var plugins = new List<MarketplacePlugin>();
        foreach (var file in Directory.GetFiles(CustomPluginsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var plugin = JsonSerializer.Deserialize<MarketplacePlugin>(json, JsonDefaults.ReadOptions);
                if (plugin is not null)
                {
                    plugin.IsBuiltIn = false;
                    plugins.Add(plugin);
                }
            }
            catch { /* skip corrupted files */ }
        }
        return plugins;
    }

    // Fix: cap imported plugin size to prevent OOM from malicious URLs
    private const int MaxImportBytes = 10 * 1024 * 1024; // 10 MB

    public async Task<MarketplacePlugin?> ImportFromUrlAsync(string url, string? name = null)
    {
        // Fix: validate URL scheme to prevent SSRF against internal/file resources
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && uri.Scheme != "http"))
            return null;

        // Fix: stream-based read with hard byte limit to prevent OOM from chunked responses
        // (Content-Length is optional — chunked transfer encoding omits it entirely)
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxImportBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        int totalRead = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            totalRead += read;
            if (totalRead > MaxImportBytes) return null;
            limited.Write(buffer, 0, read);
        }
        var content = Encoding.UTF8.GetString(limited.GetBuffer(), 0, (int)limited.Length);
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var id = $"imported-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var plugin = new MarketplacePlugin
        {
            Id = id,
            Name = name ?? ExtractTitleFromMarkdown(content) ?? $"Imported {DateTime.Now:yyyy-MM-dd}",
            Description = "Imported from URL: " + url,
            Author = "user",
            Tags = ["imported"],
            IsBuiltIn = false,
            Content = content
        };

        SaveCustomPlugin(plugin);
        return plugin;
    }

    public void SaveCustomPlugin(MarketplacePlugin plugin)
    {
        Directory.CreateDirectory(CustomPluginsDir);
        var filePath = SafePluginPath(plugin.Id);
        var json = JsonSerializer.Serialize(plugin, JsonDefaults.Options);
        File.WriteAllText(filePath, json);
    }

    public void DeleteCustomPlugin(string pluginId)
    {
        var filePath = SafePluginPath(pluginId);
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    /// <summary>
    /// Returns a safe file path for a plugin ID, preventing path traversal.
    /// </summary>
    private static string SafePluginPath(string pluginId)
    {
        // Strip any directory separators and path traversal sequences
        var safeName = Path.GetFileName(pluginId);
        if (string.IsNullOrEmpty(safeName) || safeName is "." or "..")
            safeName = "plugin";
        return Path.Combine(CustomPluginsDir, $"{safeName}.json");
    }

    public HashSet<string> GetInstalledPluginIds(List<KnowledgeBaseEntry> kbEntries)
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in kbEntries)
        {
            foreach (var tag in entry.Tags)
            {
                if (tag.StartsWith("marketplace:", StringComparison.OrdinalIgnoreCase))
                    installed.Add(tag["marketplace:".Length..]);
            }
        }
        return installed;
    }

    private static string? ExtractTitleFromMarkdown(string markdown)
    {
        using var reader = new StringReader(markdown);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.StartsWith("# ") && line.Length > 2)
                return line[2..].Trim();
        }
        return null;
    }
}
