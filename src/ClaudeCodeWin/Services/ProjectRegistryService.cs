using System.IO;
using System.Text.Json;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class ProjectRegistryService
{
    private static readonly string RegistryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin", "project-registry.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private List<ProjectInfo> _projects = [];

    public IReadOnlyList<ProjectInfo> Projects => _projects;

    /// <summary>
    /// Returns the N most recently opened projects whose folders still exist on disk.
    /// </summary>
    public IReadOnlyList<ProjectInfo> GetMostRecentProjects(int count) =>
        _projects
            .Where(p => Directory.Exists(p.Path))
            .OrderByDescending(x => x.LastOpened)
            .Take(count)
            .ToList();

    public void Load()
    {
        if (!File.Exists(RegistryPath))
            return;

        try
        {
            var json = File.ReadAllText(RegistryPath);
            _projects = JsonSerializer.Deserialize<List<ProjectInfo>>(json, JsonOptions) ?? [];
        }
        catch
        {
            _projects = [];
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
            var json = JsonSerializer.Serialize(_projects, JsonOptions);
            File.WriteAllText(RegistryPath, json);
        }
        catch { }
    }

    /// <summary>
    /// Register or update a project when its folder is opened.
    /// Auto-detects git remote URL and tech stack.
    /// </summary>
    public void RegisterProject(string folderPath, GitService gitService)
    {
        var normalized = Path.GetFullPath(folderPath);
        var existing = _projects.FirstOrDefault(p =>
            string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.LastOpened = DateTime.Now;
            existing.GitRemoteUrl = DetectGitRemote(normalized, gitService) ?? existing.GitRemoteUrl;
            existing.TechStack = DetectTechStack(normalized) ?? existing.TechStack;
        }
        else
        {
            _projects.Add(new ProjectInfo
            {
                Path = normalized,
                Name = Path.GetFileName(normalized) ?? normalized,
                GitRemoteUrl = DetectGitRemote(normalized, gitService),
                TechStack = DetectTechStack(normalized),
                LastOpened = DateTime.Now,
                RegisteredAt = DateTime.Now
            });
        }

        Save();
    }

    /// <summary>
    /// Build a compact text summary for injection into the system prompt.
    /// </summary>
    public string BuildRegistrySummary()
    {
        if (_projects.Count == 0)
            return "";

        var lines = new List<string> { "## Known local projects" };
        foreach (var p in _projects.OrderByDescending(x => x.LastOpened))
        {
            var parts = new List<string> { $"- **{p.Name}** — `{p.Path}`" };
            if (!string.IsNullOrEmpty(p.Description))
                parts.Add($"  {p.Description}");

            var meta = new List<string>();
            if (!string.IsNullOrEmpty(p.GitRemoteUrl))
                meta.Add($"git: {p.GitRemoteUrl}");
            if (!string.IsNullOrEmpty(p.TechStack))
                meta.Add(p.TechStack);
            meta.Add($"last opened: {p.LastOpened:yyyy-MM-dd}");

            parts.Add($"  ({string.Join(" | ", meta)})");
            lines.Add(string.Join("\n", parts));
        }

        return string.Join("\n", lines);
    }

    private static string? DetectGitRemote(string folderPath, GitService gitService)
    {
        var remote = gitService.RunGit("config --get remote.origin.url", folderPath);
        return string.IsNullOrWhiteSpace(remote) ? null : remote.Trim();
    }

    private static string? DetectTechStack(string folderPath)
    {
        var markers = new List<string>();

        // .NET
        if (Directory.EnumerateFiles(folderPath, "*.csproj", SearchOption.AllDirectories).Any()
            || Directory.EnumerateFiles(folderPath, "*.sln", SearchOption.AllDirectories).Any())
            markers.Add(".NET");

        // Node/React/TS
        if (File.Exists(Path.Combine(folderPath, "package.json")))
        {
            markers.Add("Node.js");
            try
            {
                var pkg = File.ReadAllText(Path.Combine(folderPath, "package.json"));
                if (pkg.Contains("\"react\""))
                    markers.Add("React");
                if (pkg.Contains("\"typescript\""))
                    markers.Add("TypeScript");
            }
            catch { }
        }

        // Python
        if (File.Exists(Path.Combine(folderPath, "requirements.txt"))
            || File.Exists(Path.Combine(folderPath, "pyproject.toml"))
            || File.Exists(Path.Combine(folderPath, "setup.py")))
            markers.Add("Python");

        // Go
        if (File.Exists(Path.Combine(folderPath, "go.mod")))
            markers.Add("Go");

        // Rust
        if (File.Exists(Path.Combine(folderPath, "Cargo.toml")))
            markers.Add("Rust");

        return markers.Count > 0 ? string.Join(", ", markers) : null;
    }
}
