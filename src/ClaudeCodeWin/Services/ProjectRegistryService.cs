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
            if (!string.IsNullOrEmpty(p.Notes))
                parts.Add($"  Notes: {p.Notes}");
            lines.Add(string.Join("\n", parts));
        }

        return string.Join("\n", lines);
    }

    public void UpdateNotes(string folderPath, string? notes)
    {
        var normalized = Path.GetFullPath(folderPath);
        var project = _projects.FirstOrDefault(p =>
            string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            project.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            Save();
        }
    }

    public string? GetNotes(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath);
        return _projects.FirstOrDefault(p =>
            string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase))?.Notes;
    }

    private static readonly string[] ProjectMarkerFiles =
    [
        "*.csproj", "package.json", "go.mod", "Cargo.toml",        // .NET, Node.js, Go, Rust
        "pyproject.toml", "requirements.txt", "setup.py",           // Python
        "pom.xml", "build.gradle", "build.gradle.kts",              // Java/Kotlin
        "Gemfile", "composer.json", "Package.swift", "pubspec.yaml", // Ruby, PHP, Swift, Dart/Flutter
        "mix.exs", "Makefile.PL", "cpanfile",                       // Elixir, Perl
        "CMakeLists.txt", "Makefile", "*.xcodeproj"                  // C/C++, general, Xcode
    ];

    /// <summary>
    /// Walk up from a file path to find the project root directory.
    /// Looks for .git directory or common project marker files.
    /// Returns null if no project root is found within 10 levels.
    /// </summary>
    public static string? DetectProjectRoot(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (dir is null) return null;

            for (var i = 0; i < 10 && dir is not null; i++)
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                    return dir;

                foreach (var marker in ProjectMarkerFiles)
                {
                    if (marker.Contains('*'))
                    {
                        if (Directory.EnumerateFiles(dir, marker).Any())
                            return dir;
                    }
                    else
                    {
                        if (File.Exists(Path.Combine(dir, marker)))
                            return dir;
                    }
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }

        return null;
    }

    private static string? DetectGitRemote(string folderPath, GitService gitService)
    {
        var remote = gitService.RunGit("config --get remote.origin.url", folderPath);
        return string.IsNullOrWhiteSpace(remote) ? null : remote.Trim();
    }

    private static string? DetectTechStack(string folderPath)
    {
        var markers = new List<string>();

        bool Has(string file) => File.Exists(Path.Combine(folderPath, file));
        bool HasPattern(string pattern) =>
            Directory.EnumerateFiles(folderPath, pattern, SearchOption.AllDirectories).Any();

        // .NET
        if (HasPattern("*.csproj") || HasPattern("*.sln"))
            markers.Add(".NET");

        // Node/React/TS
        if (Has("package.json"))
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
        if (Has("requirements.txt") || Has("pyproject.toml") || Has("setup.py"))
            markers.Add("Python");

        // Go
        if (Has("go.mod"))
            markers.Add("Go");

        // Rust
        if (Has("Cargo.toml"))
            markers.Add("Rust");

        // Java/Kotlin
        if (Has("pom.xml") || Has("build.gradle") || Has("build.gradle.kts"))
            markers.Add("Java");

        // Ruby
        if (Has("Gemfile"))
            markers.Add("Ruby");

        // PHP
        if (Has("composer.json"))
            markers.Add("PHP");

        // Swift
        if (Has("Package.swift") || HasPattern("*.xcodeproj"))
            markers.Add("Swift");

        // Dart/Flutter
        if (Has("pubspec.yaml"))
        {
            try
            {
                var pubspec = File.ReadAllText(Path.Combine(folderPath, "pubspec.yaml"));
                markers.Add(pubspec.Contains("flutter") ? "Flutter" : "Dart");
            }
            catch { markers.Add("Dart"); }
        }

        // Elixir
        if (Has("mix.exs"))
            markers.Add("Elixir");

        // C/C++
        if (Has("CMakeLists.txt"))
            markers.Add("C/C++");

        return markers.Count > 0 ? string.Join(", ", markers) : null;
    }
}
