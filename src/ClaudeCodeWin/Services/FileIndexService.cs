using System.IO;

namespace ClaudeCodeWin.Services;

public class FileIndexService
{
    private List<string> _entries = [];

    public void BuildIndex(string directory)
    {
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
        if (string.IsNullOrEmpty(query) || _entries.Count == 0)
            return [];

        var startsWithMatches = new List<string>();
        var containsMatches = new List<string>();

        foreach (var entry in _entries)
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
