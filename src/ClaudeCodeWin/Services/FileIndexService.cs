using System.IO;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Provides autocomplete suggestions for project names and file paths.
/// </summary>
public class FileIndexService
{
    private List<string> _projectNames = [];
    private List<string> _filePaths = [];

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", "__pycache__", ".next",
        ".nuget", ".cache", "dist", "build", "coverage", ".tox", ".mypy_cache",
        ".pytest_cache", "target", "packages", ".idea", ".vscode"
    };

    private const int MaxFiles = 10_000;

    public void BuildIndex(string directory)
    {
        // No-op — kept for backward compatibility. Project names are set via SetProjectNames.
    }

    public void SetProjectNames(IEnumerable<string> names)
    {
        _projectNames = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Scan the working directory and build a file index for @-mention autocomplete.
    /// </summary>
    public void BuildFileIndex(string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            _filePaths = [];
            return;
        }

        var files = new List<string>(4096);
        var basePath = Path.GetFullPath(workingDirectory);
        CollectFiles(basePath, basePath, files);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        _filePaths = files;
    }

    private static void CollectFiles(string dir, string basePath, List<string> files)
    {
        if (files.Count >= MaxFiles) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (files.Count >= MaxFiles) return;
                var relative = Path.GetRelativePath(basePath, file).Replace('\\', '/');
                files.Add(relative);
            }

            foreach (var subDir in Directory.EnumerateDirectories(dir))
            {
                if (files.Count >= MaxFiles) return;
                var dirName = Path.GetFileName(subDir);
                if (ExcludedDirs.Contains(dirName) || dirName.StartsWith('.'))
                    continue;
                CollectFiles(subDir, basePath, files);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// Search file paths for @-mention autocomplete. Empty query returns top-level files.
    /// </summary>
    public List<string> SearchFiles(string query, int maxResults = 12)
    {
        if (_filePaths.Count == 0)
            return [];

        // Empty query after @ — show first N files
        if (string.IsNullOrEmpty(query))
            return _filePaths.Take(maxResults).ToList();

        var fileNameMatches = new List<string>();
        var pathStartMatches = new List<string>();
        var pathContainsMatches = new List<string>();

        foreach (var path in _filePaths)
        {
            var fileName = Path.GetFileName(path);

            if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                fileNameMatches.Add(path);
            else if (path.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                pathStartMatches.Add(path);
            else if (path.Contains(query, StringComparison.OrdinalIgnoreCase))
                pathContainsMatches.Add(path);
        }

        var results = new List<string>(maxResults);
        AddUpTo(results, fileNameMatches, maxResults);
        AddUpTo(results, pathStartMatches, maxResults);
        AddUpTo(results, pathContainsMatches, maxResults);

        return results;
    }

    public List<string> Search(string query, int maxResults = 8)
    {
        if (string.IsNullOrEmpty(query) || query.Length < 2 || _projectNames.Count == 0)
            return [];

        var startsWithMatches = new List<string>();
        var containsMatches = new List<string>();

        foreach (var name in _projectNames)
        {
            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                startsWithMatches.Add(name);
            else if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                containsMatches.Add(name);
        }

        var results = new List<string>(maxResults);
        AddUpTo(results, startsWithMatches, maxResults);
        AddUpTo(results, containsMatches, maxResults);

        // Dismiss if any result equals query exactly (user already typed a valid project name)
        if (results.Exists(r => string.Equals(r, query, StringComparison.OrdinalIgnoreCase)))
            return [];

        return results;
    }

    private static void AddUpTo(List<string> target, List<string> source, int max)
    {
        foreach (var item in source)
        {
            if (target.Count >= max) return;
            target.Add(item);
        }
    }
}
