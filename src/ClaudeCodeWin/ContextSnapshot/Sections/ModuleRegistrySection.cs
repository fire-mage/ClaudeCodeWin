using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot.Sections;

public class ModuleRegistrySection : ISnapshotSection
{
    public string Title => "Module Registry";

    public void Generate(MarkdownBuilder md, List<AnalysisResult> results, string basePath)
    {
        md.Header(2, "3. Module Registry");

        foreach (var result in results)
        {
            if (result.ProjectType != "aspnet-core")
                continue;

            var controllers = result.Classes
                .Where(c => c.Name.EndsWith("Controller"))
                .OrderBy(c => c.Name)
                .ToList();

            if (controllers.Count > 0)
            {
                md.Header(3, $"{result.ProjectName} — Controllers");

                var noiseDeps = new HashSet<string> { "ILogger", "IConfiguration", "IWebHostEnvironment" };
                var rows = controllers.Select(c =>
                {
                    var route = c.RouteTemplate ?? "-";
                    var auth = c.Attributes.Contains("Authorize") ? "Yes" : "No";
                    var filteredDeps = c.Dependencies.Where(d => !noiseDeps.Contains(d)).ToList();
                    var deps = filteredDeps.Count > 0
                        ? string.Join(", ", filteredDeps)
                        : "-";
                    var endpointCount = result.Endpoints.Count(e => e.ControllerName == c.Name);
                    return new[] { c.Name, route, auth, deps, endpointCount.ToString() };
                }).ToList();

                md.Table(["Controller", "Route", "Auth", "Dependencies", "Endpoints"], rows);
            }

            var services = result.Classes
                .Where(c => c.Kind == "class" &&
                            !c.Name.EndsWith("Controller") &&
                            !c.FilePath.Replace('\\', '/').Contains("/Models/") &&
                            (c.FilePath.Replace('\\', '/').Contains("/Services/") ||
                             c.Interfaces.Any(i => i.StartsWith("I"))) &&
                            (c.Methods.Count > 0 || c.Dependencies.Count > 0 ||
                             c.Interfaces.Any(i => i.StartsWith("I"))))
                .OrderBy(c => c.Name)
                .ToList();

            if (services.Count > 0)
            {
                md.Header(3, $"{result.ProjectName} — Services");

                var svcNoiseDeps = new HashSet<string> { "ILogger", "IConfiguration", "IWebHostEnvironment", "string", "int", "bool" };
                var rows = services.Select(s =>
                {
                    var iface = s.Interfaces.FirstOrDefault(i => i.StartsWith("I")) ?? "-";
                    var filteredDeps = s.Dependencies.Where(d => !svcNoiseDeps.Contains(d)).ToList();
                    var deps = filteredDeps.Count > 0
                        ? string.Join(", ", filteredDeps)
                        : "-";
                    var methods = s.Methods.Count.ToString();
                    return new[] { s.Name, iface, deps, methods };
                }).ToList();

                md.Table(["Service", "Interface", "Dependencies", "Methods"], rows);
            }
        }
    }
}
