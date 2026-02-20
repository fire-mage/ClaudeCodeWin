using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot.Sections;

public class EntityOverviewSection : ISnapshotSection
{
    public string Title => "Entity Overview";

    public void Generate(MarkdownBuilder md, List<AnalysisResult> results, string basePath)
    {
        md.Header(2, "4. Entity Overview");

        foreach (var result in results)
        {
            if (result.ProjectType != "aspnet-core")
                continue;

            var entities = result.Classes
                .Where(c => c.Kind == "class" &&
                            c.FilePath.Replace('\\', '/').Contains("/Models/Entities/"))
                .OrderBy(c => c.Name)
                .ToList();

            var otherEntities = result.Classes
                .Where(c => c.Kind == "class" &&
                            !c.FilePath.Replace('\\', '/').Contains("/Models/Entities/") &&
                            (c.BaseType == "IdentityUser" ||
                             c.Properties.Any(p => p.IsNavigation)))
                .OrderBy(c => c.Name)
                .ToList();

            entities.AddRange(otherEntities);

            if (entities.Count == 0)
                continue;

            md.Header(3, $"{result.ProjectName} — Entities");

            var rows = entities.Select(e =>
            {
                var keyProps = e.Properties
                    .Where(p => !p.IsNavigation)
                    .Take(4)
                    .Select(p => $"{p.Name}: {p.Type}");

                var navPropsAll = e.Properties
                    .Where(p => p.IsNavigation)
                    .Select(p => p.IsCollection ? $"*{p.Name}" : p.Name)
                    .ToList();
                var navDisplay = string.Join(", ", navPropsAll.Take(4));
                if (navPropsAll.Count > 4)
                    navDisplay += $" (+{navPropsAll.Count - 4})";

                var baseType = e.BaseType ?? "-";

                var propsDisplay = string.Join(", ", keyProps);
                var totalProps = e.Properties.Count(p => !p.IsNavigation);
                if (totalProps > 4)
                    propsDisplay += $" (+{totalProps - 4})";

                return new[]
                {
                    e.Name,
                    baseType,
                    propsDisplay,
                    navDisplay
                };
            }).ToList();

            md.Table(["Entity", "Base", "Key Properties", "Relationships"], rows);
        }
    }
}
