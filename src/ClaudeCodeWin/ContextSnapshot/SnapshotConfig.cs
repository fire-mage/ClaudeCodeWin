using System.IO;
using System.Text.Json;

namespace ClaudeCodeWin.ContextSnapshot;

public class SnapshotConfig
{
    public string OutputPath { get; set; } = "CONTEXT_SNAPSHOT.md";
    public string StateFilePath { get; set; } = ".claude/snapshot-state.json";
    public List<ProjectConfig> Projects { get; set; } = [];
    public List<string> GlobalIgnore { get; set; } = [];
    public Dictionary<string, string> Annotations { get; set; } = [];
}

public class ProjectConfig
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public List<string> Include { get; set; } = [];
    public List<string> Exclude { get; set; } = [];
}

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static SnapshotConfig? Load(string configPath)
    {
        if (!File.Exists(configPath))
            return null;

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<SnapshotConfig>(json, JsonOptions);
            if (config == null || config.Projects.Count == 0)
                return null;

            return config;
        }
        catch
        {
            return null;
        }
    }
}
