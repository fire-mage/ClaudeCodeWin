using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class IdeasStorageService
{
    private static readonly string IdeasDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin", "ideas");

    private readonly object _lock = new();

    public IdeasDocument Load(string projectPath)
    {
        lock (_lock)
        {
            var path = GetFilePath(projectPath);
            if (!File.Exists(path))
                return new IdeasDocument { ProjectPath = projectPath };

            try
            {
                var json = File.ReadAllText(path);
                var doc = JsonSerializer.Deserialize<IdeasDocument>(json, JsonDefaults.ReadOptions);
                return doc ?? new IdeasDocument { ProjectPath = projectPath };
            }
            catch
            {
                return new IdeasDocument { ProjectPath = projectPath };
            }
        }
    }

    public void Save(string projectPath, string text)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(IdeasDir);

                var doc = new IdeasDocument
                {
                    ProjectPath = projectPath,
                    Text = text,
                    LastSavedAt = DateTime.Now
                };

                var json = JsonSerializer.Serialize(doc, JsonDefaults.Options);
                File.WriteAllText(GetFilePath(projectPath), json);
            }
            catch
            {
                // Timer callback must not throw — IO errors (OneDrive lock, disk full) are non-fatal
            }
        }
    }

    private static string GetFilePath(string projectPath)
    {
        // Normalize casing so the same path always maps to the same file on Windows
        var normalized = projectPath.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return Path.Combine(IdeasDir, $"{hash}.json");
    }
}
