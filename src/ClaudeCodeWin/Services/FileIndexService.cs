using System.IO;

namespace ClaudeCodeWin.Services;

public class FileIndexService
{
    private List<string> _entries = [];
    private string? _rootDirectory;

    public void BuildIndex(string directory)
    {
        _rootDirectory = directory;

        if (!Directory.Exists(directory))
        {
            _entries = [];
            return;
        }

        var entries = new List<string>();

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                    continue;

                if (Directory.Exists(entry))
                    entries.Add(name + "/");
                else
                    entries.Add(name);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        entries.Sort(StringComparer.OrdinalIgnoreCase);
        _entries = entries;
    }

    public List<string> Search(string query, int maxResults = 8)
    {
        if (string.IsNullOrEmpty(query))
            return [];

        // Path drill-down: if query contains "/", browse subdirectory
        if (query.Contains('/') && _rootDirectory is not null)
        {
            var lastSlash = query.LastIndexOf('/');
            var prefix = query[..(lastSlash + 1)]; // e.g. "src/"
            var searchTerm = query[(lastSlash + 1)..]; // e.g. "comp"

            var subDir = Path.Combine(_rootDirectory, prefix.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(subDir))
                return [];

            var subEntries = ListDirectory(subDir);
            var results = FilterEntries(subEntries, searchTerm, maxResults);

            // Prepend prefix to results
            for (int i = 0; i < results.Count; i++)
                results[i] = prefix + results[i];

            // Dismiss: if single result equals query exactly
            if (results.Count == 1 && results[0] == query)
                return [];

            return results;
        }

        // Standard search on root entries
        if (_entries.Count == 0)
            return [];

        var rootResults = FilterEntries(_entries, query, maxResults);

        // Dismiss: if single result equals query exactly
        if (rootResults.Count == 1 && rootResults[0] == query)
            return [];

        return rootResults;
    }

    private static List<string> ListDirectory(string directory)
    {
        var entries = new List<string>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                    continue;

                if (Directory.Exists(entry))
                    entries.Add(name + "/");
                else
                    entries.Add(name);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return entries;
    }

    private static List<string> FilterEntries(List<string> entries, string query, int maxResults)
    {
        if (string.IsNullOrEmpty(query))
        {
            // No filter — return all entries up to max
            var all = new List<string>(Math.Min(entries.Count, maxResults));
            AddUpTo(all, entries, maxResults);
            return all;
        }

        var startsWithMatches = new List<string>();
        var containsMatches = new List<string>();

        foreach (var entry in entries)
        {
            if (entry.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                startsWithMatches.Add(entry);
            else if (entry.Contains(query, StringComparison.OrdinalIgnoreCase))
                containsMatches.Add(entry);
        }

        var results = new List<string>(maxResults);
        AddUpTo(results, startsWithMatches, maxResults);
        AddUpTo(results, containsMatches, maxResults);
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
