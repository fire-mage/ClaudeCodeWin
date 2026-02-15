using System.IO;

namespace ClaudeCodeWin.ContextSnapshot;

public class ContextSnapshotService
{
    private readonly Dictionary<string, string> _cache = new();
    private readonly object _lock = new();

    /// <summary>
    /// Returns cached snapshot markdown or null if not yet generated.
    /// </summary>
    public string? GetCachedSnapshot(string projectPath)
    {
        lock (_lock)
            return _cache.TryGetValue(projectPath, out var s) ? s : null;
    }

    /// <summary>
    /// Generates snapshot for the given project path and caches it.
    /// Call from Task.Run — this method is blocking.
    /// </summary>
    public void Generate(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
            return;

        try
        {
            var config = LoadOrAutoDetect(projectPath);
            if (config == null) return;

            var generator = new SnapshotGenerator(config, projectPath);
            var markdown = generator.Generate();

            lock (_lock)
                _cache[projectPath] = markdown;
        }
        catch
        {
            // Snapshot generation is optional — silently fail
        }
    }

    /// <summary>
    /// Generates snapshots for multiple project paths (blocking).
    /// Call from Task.Run.
    /// </summary>
    public void GenerateForProjects(IEnumerable<string> projectPaths)
    {
        foreach (var path in projectPaths)
            Generate(path);
    }

    /// <summary>
    /// Returns a combined markdown string with snapshots for the given project paths,
    /// and the count of projects that had cached snapshots.
    /// </summary>
    public (string? markdown, int count) GetCombinedSnapshot(IEnumerable<string> projectPaths)
    {
        var parts = new List<string>();
        lock (_lock)
        {
            foreach (var path in projectPaths)
            {
                if (_cache.TryGetValue(path, out var snapshot))
                    parts.Add($"## Project: {Path.GetFileName(path)}\n\n{snapshot}");
            }
        }

        return parts.Count > 0
            ? (string.Join("\n\n---\n\n", parts), parts.Count)
            : (null, 0);
    }

    /// <summary>
    /// Invalidates cached snapshot for a project, forcing regeneration on next Generate call.
    /// </summary>
    public void Invalidate(string projectPath)
    {
        lock (_lock)
            _cache.Remove(projectPath);
    }

    private static SnapshotConfig? LoadOrAutoDetect(string projectPath)
    {
        // 1. Try loading explicit snapshot-config.json
        var configPath = Path.Combine(projectPath, "snapshot-config.json");
        var config = ConfigLoader.Load(configPath);
        if (config != null)
            return config;

        // 2. Auto-detect from project markers (.csproj, package.json, etc.)
        return AutoDetector.Detect(projectPath);
    }
}
