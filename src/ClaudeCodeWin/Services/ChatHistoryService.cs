using System.IO;
using System.Text.Json;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class ChatHistoryService
{
    private static readonly string HistoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin", "history");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ChatHistoryService()
    {
        Directory.CreateDirectory(HistoryDir);
    }

    public List<ChatHistorySummary> ListAll()
    {
        var summaries = new List<ChatHistorySummary>();

        foreach (var file in Directory.EnumerateFiles(HistoryDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var entry = JsonSerializer.Deserialize<ChatHistoryEntry>(json, JsonOptions);
                if (entry is null) continue;

                summaries.Add(new ChatHistorySummary
                {
                    Id = entry.Id,
                    Title = entry.Title,
                    ProjectPath = entry.ProjectPath,
                    CreatedAt = entry.CreatedAt,
                    UpdatedAt = entry.UpdatedAt,
                    MessageCount = entry.Messages.Count
                });
            }
            catch (JsonException) { }
            catch (IOException) { }
        }

        summaries.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return summaries;
    }

    public ChatHistoryEntry? Load(string id)
    {
        var path = Path.Combine(HistoryDir, $"{id}.json");
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ChatHistoryEntry>(json, JsonOptions);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public void Save(ChatHistoryEntry entry)
    {
        entry.UpdatedAt = DateTime.Now;
        var path = Path.Combine(HistoryDir, $"{entry.Id}.json");
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        File.WriteAllText(path, json);
    }

    public void Delete(string id)
    {
        var path = Path.Combine(HistoryDir, $"{id}.json");
        if (File.Exists(path))
            File.Delete(path);
    }
}
