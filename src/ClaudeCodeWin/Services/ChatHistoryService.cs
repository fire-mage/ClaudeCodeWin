using System.IO;
using System.Text;
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
        var processedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Read encrypted .dat files
        foreach (var file in Directory.EnumerateFiles(HistoryDir, "*.dat"))
        {
            try
            {
                var entry = LoadFromDat(file);
                if (entry is null) continue;

                processedIds.Add(entry.Id);
                summaries.Add(ToSummary(entry));
            }
            catch { }
        }

        // Read legacy .json files and migrate them
        foreach (var file in Directory.EnumerateFiles(HistoryDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var entry = JsonSerializer.Deserialize<ChatHistoryEntry>(json, JsonOptions);
                if (entry is null) continue;

                if (processedIds.Contains(entry.Id))
                    continue; // Already have .dat version

                // Migrate: save as .dat, delete .json
                SaveToDat(entry);
                File.Delete(file);

                processedIds.Add(entry.Id);
                summaries.Add(ToSummary(entry));
            }
            catch { }
        }

        summaries.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return summaries;
    }

    public ChatHistoryEntry? Load(string id)
    {
        // Try .dat first
        var datPath = Path.Combine(HistoryDir, $"{id}.dat");
        if (File.Exists(datPath))
            return LoadFromDat(datPath);

        // Fallback to legacy .json
        var jsonPath = Path.Combine(HistoryDir, $"{id}.json");
        if (!File.Exists(jsonPath)) return null;

        try
        {
            var json = File.ReadAllText(jsonPath);
            return JsonSerializer.Deserialize<ChatHistoryEntry>(json, JsonOptions);
        }
        catch { return null; }
    }

    public void Save(ChatHistoryEntry entry)
    {
        entry.UpdatedAt = DateTime.Now;
        SaveToDat(entry);
    }

    public void Delete(string id)
    {
        var datPath = Path.Combine(HistoryDir, $"{id}.dat");
        if (File.Exists(datPath))
            File.Delete(datPath);

        var jsonPath = Path.Combine(HistoryDir, $"{id}.json");
        if (File.Exists(jsonPath))
            File.Delete(jsonPath);
    }

    private static void SaveToDat(ChatHistoryEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = SettingsService.DpapiProtect(jsonBytes);
        var path = Path.Combine(HistoryDir, $"{entry.Id}.dat");
        File.WriteAllBytes(path, protectedBytes);
    }

    private static ChatHistoryEntry? LoadFromDat(string path)
    {
        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var jsonBytes = SettingsService.DpapiUnprotect(protectedBytes);
            if (jsonBytes.Length == 0) return null;
            var json = Encoding.UTF8.GetString(jsonBytes);
            return JsonSerializer.Deserialize<ChatHistoryEntry>(json, JsonOptions);
        }
        catch { return null; }
    }

    private static ChatHistorySummary ToSummary(ChatHistoryEntry entry) => new()
    {
        Id = entry.Id,
        Title = entry.Title,
        ProjectPath = entry.ProjectPath,
        CreatedAt = entry.CreatedAt,
        UpdatedAt = entry.UpdatedAt,
        MessageCount = entry.Messages.Count
    };
}
