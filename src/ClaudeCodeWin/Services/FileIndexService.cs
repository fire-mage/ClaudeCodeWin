namespace ClaudeCodeWin.Services;

/// <summary>
/// Provides autocomplete suggestions based on project names from the registry.
/// </summary>
public class FileIndexService
{
    private List<string> _projectNames = [];

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

        // Dismiss if single result equals query exactly
        if (results.Count == 1 && string.Equals(results[0], query, StringComparison.OrdinalIgnoreCase))
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
