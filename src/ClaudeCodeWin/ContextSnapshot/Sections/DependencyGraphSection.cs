using System.Text;
using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot.Sections;

public class DependencyGraphSection : ISnapshotSection
{
    public string Title => "Dependency Graph";

    public void Generate(MarkdownBuilder md, List<AnalysisResult> results, string basePath)
    {
        md.Header(2, "7. Dependency Graph");

        foreach (var result in results)
        {
            if (result.Dependencies.Count == 0)
                continue;

            md.Header(3, result.ProjectName);

            var groups = result.Dependencies
                .GroupBy(d => d.From)
                .OrderBy(g => g.Key)
                .ToList();

            var noiseDeps = new HashSet<string>(StringComparer.Ordinal)
            {
                "ILogger", "IConfiguration", "IWebHostEnvironment", "IHttpContextAccessor",
                "string", "int", "bool", "RequestDelegate"
            };

            var lines = new StringBuilder();
            foreach (var group in groups)
            {
                var targets = group
                    .Select(d => d.To)
                    .Distinct()
                    .Where(t => !noiseDeps.Contains(t))
                    .OrderBy(t => t)
                    .ToList();

                if (targets.Count == 0)
                    continue;

                lines.AppendLine($"{group.Key} → {string.Join(", ", targets)}");
            }

            md.CodeBlock(lines.ToString().TrimEnd());
        }
    }
}
