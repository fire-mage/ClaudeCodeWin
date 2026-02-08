using System.IO;

namespace ClaudeCodeWin.Services;

public class FileIndexService
{
    private List<string> _files = [];
    private string? _indexedDirectory;

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", "packages",
        "__pycache__", ".idea", ".vscode", "dist", "build",
        ".next", ".nuget", "TestResults", ".angular", "coverage",
        ".svn", ".hg", "vendor", "target"
    };

    private const int MaxDepth = 12;
    private const int MaxFiles = 30_000;

    public void BuildIndex(string directory)
    {
        if (!Directory.Exists(directory))
        {
            _files = [];
            _indexedDirectory = null;
            return;
        }

        var files = new List<string>();
        var basePath = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Scan(basePath, basePath, 0, files);
        _files = files;
        _indexedDirectory = directory;
    }

    private static void Scan(string basePath, string currentDir, int depth, List<string> results)
    {
        if (depth > MaxDepth || results.Count >= MaxFiles)
            return;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(currentDir))
            {
                if (results.Count >= MaxFiles)
                    return;

                var name = Path.GetFileName(entry);

                if (Directory.Exists(entry))
                {
                    if (ExcludedDirs.Contains(name))
                        continue;
                    Scan(basePath, entry, depth + 1, results);
                }
                else
                {
                    // Store relative path with forward slashes
                    var relative = entry[(basePath.Length + 1)..].Replace('\\', '/');
                    results.Add(relative);
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
    }

    public List<string> Search(string query, int maxResults = 8)
    {
        if (string.IsNullOrEmpty(query) || _files.Count == 0)
            return [];

        var normalizedQuery = query.Replace('\\', '/');
        var isPathQuery = normalizedQuery.Contains('/');

        var nameStartMatches = new List<string>();
        var nameContainsMatches = new List<string>();
        var pathContainsMatches = new List<string>();

        foreach (var file in _files)
        {
            if (isPathQuery)
            {
                if (file.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                    pathContainsMatches.Add(file);
            }
            else
            {
                var fileName = GetFileName(file);
                if (fileName.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                    nameStartMatches.Add(file);
                else if (fileName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                    nameContainsMatches.Add(file);
                else if (file.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                    pathContainsMatches.Add(file);
            }
        }

        var results = new List<string>(maxResults);
        AddUpTo(results, nameStartMatches, maxResults);
        AddUpTo(results, nameContainsMatches, maxResults);
        AddUpTo(results, pathContainsMatches, maxResults);
        return results;
    }

    private static string GetFileName(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
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
