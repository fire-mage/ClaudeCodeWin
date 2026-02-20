using System.IO;
using System.Text;
using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot.Sections;

public class FileTreeSection : ISnapshotSection
{
    private readonly SnapshotConfig _config;

    public string Title => "File Tree";

    public FileTreeSection(SnapshotConfig config)
    {
        _config = config;
    }

    public void Generate(MarkdownBuilder md, List<AnalysisResult> results, string basePath)
    {
        md.Header(2, "2. File Tree");

        foreach (var result in results)
        {
            md.Line($"**{result.ProjectName}** ({result.ScannedFiles.Count} files)");
            md.Line();

            var projectConfig = _config.Projects.First(p => p.Name == result.ProjectName);
            var projectBasePath = Path.GetFullPath(Path.Combine(basePath, projectConfig.Path));

            var tree = BuildTree(result.ScannedFiles, projectBasePath);
            md.CodeBlock(tree);
        }
    }

    private string BuildTree(List<string> files, string basePath)
    {
        var dirGroups = new SortedDictionary<string, List<string>>();

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(basePath, file).Replace('\\', '/');
            var dir = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? ".";
            if (string.IsNullOrEmpty(dir)) dir = ".";

            if (!dirGroups.ContainsKey(dir))
                dirGroups[dir] = [];

            dirGroups[dir].Add(Path.GetFileName(file));
        }

        var sb = new StringBuilder();

        foreach (var (dir, dirFiles) in dirGroups)
        {
            var annotation = GetAnnotation(dir);
            var annotationSuffix = annotation != null ? $"  — {annotation}" : "";

            if (dir == ".")
            {
                foreach (var f in dirFiles.OrderBy(f => f))
                    sb.AppendLine($"  {f}");
            }
            else
            {
                sb.AppendLine($"  {dir}/{annotationSuffix} ({dirFiles.Count})");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private string? GetAnnotation(string dir)
    {
        string? bestAnnotation = null;
        int bestLength = -1;
        bool bestExact = false;

        foreach (var (pattern, annotation) in _config.Annotations)
        {
            var normalizedPattern = pattern.TrimEnd('/');

            bool matchExact = dir.Equals(normalizedPattern, StringComparison.Ordinal) ||
                              dir.StartsWith(normalizedPattern + "/", StringComparison.Ordinal) ||
                              dir.EndsWith("/" + normalizedPattern, StringComparison.Ordinal);

            bool matchInsensitive = !matchExact && (
                              dir.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase) ||
                              dir.StartsWith(normalizedPattern + "/", StringComparison.OrdinalIgnoreCase) ||
                              dir.EndsWith("/" + normalizedPattern, StringComparison.OrdinalIgnoreCase));

            if (matchExact || matchInsensitive)
            {
                bool isExact = matchExact;
                if (normalizedPattern.Length > bestLength ||
                    (normalizedPattern.Length == bestLength && isExact && !bestExact))
                {
                    bestAnnotation = annotation;
                    bestLength = normalizedPattern.Length;
                    bestExact = isExact;
                }
            }
        }

        return bestAnnotation;
    }
}
