using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot.Sections;

public class ApiSurfaceSection : ISnapshotSection
{
    public string Title => "API Surface";

    public void Generate(MarkdownBuilder md, List<AnalysisResult> results, string basePath)
    {
        md.Header(2, "5. API Surface");

        var allEndpoints = results
            .SelectMany(r => r.Endpoints)
            .OrderBy(e => e.Path)
            .ThenBy(e => e.HttpMethod)
            .ToList();

        if (allEndpoints.Count == 0)
        {
            md.Line("*No endpoints found*");
            return;
        }

        md.Line($"Total: **{allEndpoints.Count}** endpoints");
        md.Line();

        var byController = allEndpoints
            .GroupBy(e => e.ControllerName)
            .OrderBy(g => g.Key)
            .ToList();

        var rows = byController.Select(g =>
        {
            var methods = string.Join(" ", g.Select(e => e.HttpMethod).Distinct().OrderBy(m => m));
            var paths = string.Join(", ", g.Select(e => e.Path).Distinct().Take(4));
            if (g.Count() > 4)
                paths += $" (+{g.Count() - 4})";
            var auth = g.All(e => e.RequiresAuth) ? "Yes" : g.Any(e => e.RequiresAuth) ? "Mixed" : "No";
            return new[] { g.Key, methods, g.Count().ToString(), paths, auth };
        }).ToList();

        md.Table(["Controller", "Methods", "Count", "Paths (sample)", "Auth"], rows);
    }
}
