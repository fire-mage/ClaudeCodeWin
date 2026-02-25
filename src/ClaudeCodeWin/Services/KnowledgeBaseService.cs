using System.IO;
using System.Text.Json;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class KnowledgeBaseService
{
    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string GetKnowledgeBaseDir(string workingDir)
    {
        var encoded = InstructionsService.EncodePath(Path.GetFullPath(workingDir));
        return Path.Combine(UserProfile, ".claude", "projects", encoded, "memory", "knowledge-base");
    }

    public string GetIndexPath(string workingDir) =>
        Path.Combine(GetKnowledgeBaseDir(workingDir), "_index.json");

    public List<KnowledgeBaseEntry> LoadEntries(string workingDir)
    {
        var indexPath = GetIndexPath(workingDir);
        if (!File.Exists(indexPath))
            return [];

        try
        {
            var json = File.ReadAllText(indexPath);
            return JsonSerializer.Deserialize<List<KnowledgeBaseEntry>>(json, JsonDefaults.ReadOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
