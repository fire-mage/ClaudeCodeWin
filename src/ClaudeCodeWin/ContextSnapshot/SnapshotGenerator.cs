using System.IO;
using ClaudeCodeWin.ContextSnapshot.Models;
using ClaudeCodeWin.ContextSnapshot.Sections;

namespace ClaudeCodeWin.ContextSnapshot;

public class SnapshotGenerator
{
    private readonly SnapshotConfig _config;
    private readonly string _basePath;
    private readonly List<IFileAnalyzer> _analyzers;
    private readonly List<ISnapshotSection> _sections;

    public SnapshotGenerator(SnapshotConfig config, string basePath)
    {
        _config = config;
        _basePath = basePath;

        _analyzers =
        [
            new CSharpAnalyzer(),
            new TypeScriptAnalyzer()
        ];

        _sections =
        [
            new WorkStateSection(config.StateFilePath),
            new FileTreeSection(config),
            new ModuleRegistrySection(),
            new EntityOverviewSection(),
            new ApiSurfaceSection(),
            new FrontendSurfaceSection(),
            new DependencyGraphSection(),
            new CodePatternsSection(),
            new RecentChangesSection(),
            new ConfigurationSection()
        ];
    }

    public string Generate()
    {
        var filesByProject = FileScanner.ScanAllProjects(_config, _basePath);

        var results = new List<AnalysisResult>();
        int totalCs = 0, totalTs = 0;

        foreach (var project in _config.Projects)
        {
            if (!filesByProject.TryGetValue(project.Name, out var files))
                continue;

            var result = new AnalysisResult
            {
                ProjectName = project.Name,
                ProjectType = project.Type,
                ScannedFiles = files
            };

            foreach (var file in files)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    foreach (var analyzer in _analyzers)
                    {
                        if (analyzer.CanAnalyze(file))
                            analyzer.Analyze(file, content, result);
                    }
                }
                catch
                {
                    // Skip unreadable files
                }
            }

            totalCs += files.Count(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
            totalTs += files.Count(f => f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                                       f.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase));

            results.Add(result);
        }

        var md = new MarkdownBuilder();

        md.Line("# Context Snapshot");
        md.Line();

        foreach (var section in _sections)
        {
            section.Generate(md, results, _basePath);
        }

        var output = md.ToString();
        var sizeKb = System.Text.Encoding.UTF8.GetByteCount(output) / 1024.0;

        // Prepend stats after the header
        var statsLine = $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm} | {totalCs} C# | {totalTs} TS/TSX | Size: {sizeKb:F1}KB";
        output = output.Replace("# Context Snapshot\n",
            $"# Context Snapshot\n\n{statsLine}\n\n---\n");

        return output;
    }
}
