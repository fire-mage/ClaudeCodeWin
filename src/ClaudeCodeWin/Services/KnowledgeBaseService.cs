using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class KnowledgeBaseService
{
    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // Per-directory lock to prevent TOCTOU race when concurrent callers save entries
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _dirLocks = new(StringComparer.OrdinalIgnoreCase);

    private VectorMemoryService? _vectorMemory;

    public void SetVectorMemory(VectorMemoryService vectorMemory) => _vectorMemory = vectorMemory;

    private static readonly Regex SafeIdPattern = new(@"^[a-zA-Z0-9][a-zA-Z0-9_-]*$", RegexOptions.Compiled);
    private static readonly Regex ReservedNames = new(@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns true if the article ID is safe to use as a filename component.
    /// Only allows alphanumeric, hyphens, and underscores.
    /// </summary>
    public static bool IsValidArticleId(string id) =>
        !string.IsNullOrEmpty(id) && SafeIdPattern.IsMatch(id) && !ReservedNames.IsMatch(id);

    public string GetKnowledgeBaseDir(string workingDir)
    {
        var encoded = InstructionsService.EncodePath(Path.GetFullPath(workingDir));
        return Path.Combine(UserProfile, ".claude", "projects", encoded, "memory", "knowledge-base");
    }

    public string GetIndexPath(string workingDir) =>
        Path.Combine(GetKnowledgeBaseDir(workingDir), "_index.json");

    public List<KnowledgeBaseEntry> LoadEntries(string workingDir)
    {
        // Use per-directory lock to prevent reading a partially-written index
        var dirLock = _dirLocks.GetOrAdd(Path.GetFullPath(workingDir), _ => new object());
        lock (dirLock)
        {
            return LoadEntriesUnsafe(workingDir);
        }
    }

    /// <summary>Load entries without locking — for use inside SaveEntry which already holds the lock.</summary>
    private List<KnowledgeBaseEntry> LoadEntriesUnsafe(string workingDir)
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

    /// <summary>
    /// Save or update a KB article and its index entry.
    /// If an entry with the same ID exists, it is updated; otherwise a new one is created.
    /// </summary>
    public bool SaveEntry(string workingDir, KnowledgeBaseEntry entry, string content)
    {
        // Per-directory lock prevents TOCTOU race when concurrent callers (onboarding + technical writer) save simultaneously
        var dirLock = _dirLocks.GetOrAdd(Path.GetFullPath(workingDir), _ => new object());
        lock (dirLock)
        {
            try
            {
                var kbDir = GetKnowledgeBaseDir(workingDir);
                Directory.CreateDirectory(kbDir);

                if (!IsValidArticleId(entry.Id))
                {
                    DiagnosticLogger.Log("KB_SAVE_ERROR", $"Invalid article ID rejected: {entry.Id}");
                    return false;
                }

                var fileName = string.IsNullOrEmpty(entry.File) ? $"{entry.Id}.md" : entry.File;
                var safeName = Path.GetFileName(fileName);
                if (string.IsNullOrEmpty(safeName) || safeName != fileName)
                {
                    DiagnosticLogger.Log("KB_SAVE_ERROR", $"Invalid article filename rejected: {fileName}");
                    return false;
                }

                entry.File = safeName;
                File.WriteAllText(Path.Combine(kbDir, safeName), content);

                // Update index (use unsafe variant — we already hold the dir lock)
                var entries = LoadEntriesUnsafe(workingDir);
                var existing = entries.FindIndex(e => e.Id == entry.Id);
                if (existing >= 0)
                    entries[existing] = entry;
                else
                    entries.Add(entry);

                var json = JsonSerializer.Serialize(entries, JsonDefaults.Options);
                File.WriteAllText(GetIndexPath(workingDir), json);

                // Fire-and-forget: index article in vector memory for semantic search
                if (_vectorMemory?.IsAvailable == true)
                {
                    var vm = _vectorMemory;
                    var wd = workingDir;
                    var id = entry.Id;
                    var txt = content;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            vm.IndexDocument(wd, "kb", id, txt,
                                new Dictionary<string, string> { ["title"] = id });
                        }
                        catch (Exception ex) { DiagnosticLogger.Log("KB_VECTOR_INDEX_ERROR", ex.Message); }
                    });
                }

                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("KB_SAVE_ERROR", $"{entry.Id}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Read article content by ID.
    /// </summary>
    public string? GetArticleContent(string workingDir, string articleId)
    {
        var entries = LoadEntries(workingDir);
        var entry = entries.FirstOrDefault(e => e.Id == articleId);
        if (entry is null) return null;

        var safeName = Path.GetFileName(entry.File);
        if (string.IsNullOrEmpty(safeName) || safeName != entry.File) return null;
        var filePath = Path.Combine(GetKnowledgeBaseDir(workingDir), safeName);
        return File.Exists(filePath) ? File.ReadAllText(filePath) : null;
    }
}
