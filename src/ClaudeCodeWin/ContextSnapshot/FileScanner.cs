using System.IO;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace ClaudeCodeWin.ContextSnapshot;

public static class FileScanner
{
    public static List<string> ScanProject(ProjectConfig project, string basePath, List<string> globalIgnore)
    {
        var projectPath = Path.GetFullPath(Path.Combine(basePath, project.Path));
        if (!Directory.Exists(projectPath))
            return [];

        var matcher = new Matcher();

        foreach (var pattern in project.Include)
            matcher.AddInclude(pattern);

        foreach (var pattern in project.Exclude)
            matcher.AddExclude(pattern);

        foreach (var ignore in globalIgnore)
            matcher.AddExclude($"**/{ignore}/**");

        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(projectPath)));

        return result.Files
            .Select(f => Path.GetFullPath(Path.Combine(projectPath, f.Path)))
            .OrderBy(f => f)
            .ToList();
    }

    public static Dictionary<string, List<string>> ScanAllProjects(SnapshotConfig config, string basePath)
    {
        var result = new Dictionary<string, List<string>>();

        foreach (var project in config.Projects)
        {
            var files = ScanProject(project, basePath, config.GlobalIgnore);
            result[project.Name] = files;
        }

        return result;
    }
}
