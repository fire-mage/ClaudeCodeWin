using System.IO;
using System.Text.Json;

namespace ClaudeCodeWin.ContextSnapshot;

public static class AutoDetector
{
    private static readonly List<string> DefaultGlobalIgnore =
        [".git", "node_modules", "bin", "obj", "dist", "publish", ".vs", ".idea", "wwwroot"];

    public static SnapshotConfig? Detect(string basePath)
    {
        var config = new SnapshotConfig
        {
            GlobalIgnore = [.. DefaultGlobalIgnore]
        };

        // Scan for .csproj files (ASP.NET Core projects)
        DetectDotNetProjects(basePath, config);

        // Scan for React/TypeScript projects
        DetectFrontendProjects(basePath, config);

        return config.Projects.Count > 0 ? config : null;
    }

    private static void DetectDotNetProjects(string basePath, SnapshotConfig config)
    {
        // Find .csproj files up to 3 levels deep
        var csprojFiles = FindFiles(basePath, "*.csproj", maxDepth: 3);

        foreach (var csproj in csprojFiles)
        {
            var projectDir = Path.GetDirectoryName(csproj)!;
            var relativePath = Path.GetRelativePath(basePath, projectDir).Replace('\\', '/');
            if (relativePath == ".") relativePath = ".";

            var projectName = Path.GetFileNameWithoutExtension(csproj);

            // Check if it's a web/API project by looking for Controllers/ or Program.cs with WebApplication
            var isWebProject = Directory.Exists(Path.Combine(projectDir, "Controllers"))
                || Directory.Exists(Path.Combine(projectDir, "Services"))
                || HasWebApplicationMarker(projectDir);

            if (!isWebProject) continue;

            config.Projects.Add(new ProjectConfig
            {
                Name = projectName,
                Path = relativePath,
                Type = "aspnet-core",
                Include =
                [
                    "Controllers/**/*.cs",
                    "Services/**/*.cs",
                    "Models/**/*.cs",
                    "Data/ApplicationDbContext.cs",
                    "Authentication/**/*.cs",
                    "Middleware/**/*.cs",
                    "Program.cs"
                ],
                Exclude =
                [
                    "Data/Migrations/**"
                ]
            });

            // Add common annotations
            config.Annotations.TryAdd("Controllers", "API Controllers");
            config.Annotations.TryAdd("Services", "Business logic");
            config.Annotations.TryAdd("Models/Entities", "EF Core entities");
            config.Annotations.TryAdd("Models/DTOs", "Data transfer objects");
        }
    }

    private static void DetectFrontendProjects(string basePath, SnapshotConfig config)
    {
        // Find package.json files up to 3 levels deep
        var packageJsonFiles = FindFiles(basePath, "package.json", maxDepth: 3);

        foreach (var packageJson in packageJsonFiles)
        {
            var projectDir = Path.GetDirectoryName(packageJson)!;

            // Skip node_modules
            if (projectDir.Replace('\\', '/').Contains("/node_modules/"))
                continue;

            // Check if it's a React project
            if (!IsReactProject(packageJson))
                continue;

            // Look for src/ directory
            var srcDir = Path.Combine(projectDir, "src");
            if (!Directory.Exists(srcDir))
                continue;

            var relativePath = Path.GetRelativePath(basePath, srcDir).Replace('\\', '/');
            var projectName = Path.GetFileName(projectDir);

            config.Projects.Add(new ProjectConfig
            {
                Name = projectName,
                Path = relativePath,
                Type = "react-app",
                Include =
                [
                    "**/*.ts",
                    "**/*.tsx"
                ],
                Exclude = []
            });

            config.Annotations.TryAdd("pages", "React page components");
            config.Annotations.TryAdd("services", "API clients");
            config.Annotations.TryAdd("components", "Reusable UI components");
            config.Annotations.TryAdd("hooks", "Custom React hooks");
        }
    }

    private static bool HasWebApplicationMarker(string projectDir)
    {
        var programCs = Path.Combine(projectDir, "Program.cs");
        if (!File.Exists(programCs)) return false;

        try
        {
            var content = File.ReadAllText(programCs);
            return content.Contains("WebApplication") || content.Contains("WebHost")
                || content.Contains("MapControllers") || content.Contains("app.Run");
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReactProject(string packageJsonPath)
    {
        try
        {
            var content = File.ReadAllText(packageJsonPath);
            using var doc = JsonDocument.Parse(content);

            // Check dependencies for react
            if (doc.RootElement.TryGetProperty("dependencies", out var deps))
            {
                if (deps.TryGetProperty("react", out _))
                    return true;
            }

            if (doc.RootElement.TryGetProperty("devDependencies", out var devDeps))
            {
                if (devDeps.TryGetProperty("react", out _))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> FindFiles(string basePath, string pattern, int maxDepth)
    {
        var results = new List<string>();
        FindFilesRecursive(basePath, pattern, 0, maxDepth, results);
        return results;
    }

    private static void FindFilesRecursive(string dir, string pattern, int currentDepth, int maxDepth, List<string> results)
    {
        if (currentDepth > maxDepth) return;

        try
        {
            results.AddRange(Directory.GetFiles(dir, pattern));

            if (currentDepth < maxDepth)
            {
                foreach (var subDir in Directory.GetDirectories(dir))
                {
                    var dirName = Path.GetFileName(subDir);
                    if (dirName is ".git" or "node_modules" or "bin" or "obj" or ".vs")
                        continue;
                    FindFilesRecursive(subDir, pattern, currentDepth + 1, maxDepth, results);
                }
            }
        }
        catch
        {
            // Skip inaccessible directories
        }
    }
}
