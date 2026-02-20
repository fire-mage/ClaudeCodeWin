using System.IO;
using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot.Sections;

public class FrontendSurfaceSection : ISnapshotSection
{
    public string Title => "Frontend Surface";

    public void Generate(MarkdownBuilder md, List<AnalysisResult> results, string basePath)
    {
        md.Header(2, "6. Frontend Surface");

        foreach (var result in results)
        {
            if (result.ProjectType != "react-app")
                continue;

            if (result.Routes.Count > 0)
            {
                md.Header(3, $"{result.ProjectName} — Routes");
                var routeRows = result.Routes.Select(r => new[]
                {
                    r.Path,
                    r.Component,
                    r.RequiresAuth ? "Auth" : "Public"
                }).ToList();

                md.Table(["Path", "Component", "Access"], routeRows);
            }

            if (result.FrontendServices.Count > 0)
            {
                md.Header(3, $"{result.ProjectName} — API Services");
                var serviceRows = result.FrontendServices
                    .OrderBy(s => s.Name)
                    .Select(s =>
                    {
                        var methods = string.Join(" ", s.ApiCalls
                            .Select(c => c.HttpMethod).Distinct().OrderBy(m => m));
                        var exports = string.Join(", ", s.Exports.Take(3));
                        if (s.Exports.Count > 3)
                            exports += $" (+{s.Exports.Count - 3})";
                        return new[] { s.Name, s.ApiCalls.Count.ToString(), methods, exports };
                    }).ToList();

                md.Table(["Service", "Calls", "Methods", "Exports"], serviceRows);
            }

            var components = result.Classes
                .Where(c => c.Kind == "component")
                .OrderBy(c => c.FilePath)
                .ToList();

            if (components.Count > 0)
            {
                md.Header(3, $"{result.ProjectName} — Components");
                var componentRows = components.Select(c =>
                {
                    var relativePath = Path.GetRelativePath(basePath, c.FilePath).Replace('\\', '/');
                    var exports = string.Join(", ", c.Methods.Select(m => m.Name).Take(3));
                    if (c.Methods.Count > 3)
                        exports += $" (+{c.Methods.Count - 3})";
                    return new[] { c.Name, relativePath, exports };
                }).ToList();

                md.Table(["Component", "Path", "Exports"], componentRows);
            }
        }
    }
}
