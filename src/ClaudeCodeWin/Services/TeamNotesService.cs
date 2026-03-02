using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class TeamNotesService
{
    private static readonly string NotesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin", "team-notes");

    private readonly object _lock = new();

    // In-memory cache of unread counts per project (avoids disk reads on every Refresh)
    private readonly ConcurrentDictionary<string, int> _unreadCache = new(StringComparer.OrdinalIgnoreCase);

    public event Action<TeamNote>? OnNoteAdded;

    public List<TeamNote> Load(string projectPath)
    {
        lock (_lock)
        {
            var notes = TryLoadLocked(projectPath);
            _unreadCache[projectPath] = notes.Count(n => !n.IsRead);
            return notes;
        }
    }

    public TeamNote AddNote(string projectPath, string role, string? featureId, string? featureTitle, string message)
    {
        var note = new TeamNote
        {
            ProjectPath = projectPath,
            SourceRole = role,
            FeatureId = featureId,
            FeatureTitle = featureTitle,
            Message = message
        };
        var saved = false;
        lock (_lock)
        {
            List<TeamNote> notes;
            try { notes = LoadLocked(projectPath); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"TeamNotes load failed, refusing to overwrite: {ex.Message}");
                return note; // Still return the note object but don't persist (avoids data loss)
            }
            notes.Add(note);
            if (SaveLocked(projectPath, notes))
            {
                _unreadCache.AddOrUpdate(projectPath, 1, (_, count) => count + 1);
                saved = true;
            }
        }
        if (saved)
            OnNoteAdded?.Invoke(note);
        return note;
    }

    /// <summary>
    /// Add multiple notes in a single file I/O operation.
    /// </summary>
    public void AddNotes(string projectPath, string role, string? featureId, string? featureTitle, List<string> messages)
    {
        if (messages.Count == 0) return;

        var added = new List<TeamNote>(messages.Count);
        bool saved;
        lock (_lock)
        {
            List<TeamNote> notes;
            try { notes = LoadLocked(projectPath); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"TeamNotes load failed, refusing to overwrite: {ex.Message}");
                return;
            }
            foreach (var message in messages)
            {
                var note = new TeamNote
                {
                    ProjectPath = projectPath,
                    SourceRole = role,
                    FeatureId = featureId,
                    FeatureTitle = featureTitle,
                    Message = message
                };
                notes.Add(note);
                added.Add(note);
            }
            saved = SaveLocked(projectPath, notes);
            if (saved)
                _unreadCache.AddOrUpdate(projectPath, added.Count, (_, count) => count + added.Count);
        }
        if (saved)
        {
            foreach (var note in added)
                OnNoteAdded?.Invoke(note);
        }
    }

    public void MarkRead(string projectPath, string noteId)
    {
        lock (_lock)
        {
            List<TeamNote> notes;
            try { notes = LoadLocked(projectPath); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"TeamNotes load failed in MarkRead: {ex.Message}");
                return;
            }
            var note = notes.FirstOrDefault(n => n.Id == noteId);
            if (note is null || note.IsRead) return;

            note.IsRead = true;
            if (SaveLocked(projectPath, notes))
                _unreadCache.AddOrUpdate(projectPath, 0, (_, count) => Math.Max(0, count - 1));
        }
    }

    public void MarkAllRead(string projectPath)
    {
        lock (_lock)
        {
            List<TeamNote> notes;
            try { notes = LoadLocked(projectPath); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"TeamNotes load failed in MarkAllRead: {ex.Message}");
                return;
            }
            var changed = false;
            foreach (var note in notes)
            {
                if (!note.IsRead)
                {
                    note.IsRead = true;
                    changed = true;
                }
            }

            if (changed && SaveLocked(projectPath, notes))
                _unreadCache[projectPath] = 0;
            else if (!changed)
                _unreadCache[projectPath] = 0;
        }
    }

    public void DismissNote(string projectPath, string noteId)
    {
        lock (_lock)
        {
            List<TeamNote> notes;
            try { notes = LoadLocked(projectPath); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"TeamNotes load failed in DismissNote: {ex.Message}");
                return;
            }
            // Check if dismissed note was unread before removing
            var wasUnread = notes.Any(n => n.Id == noteId && !n.IsRead);
            var removed = notes.RemoveAll(n => n.Id == noteId);
            if (removed > 0 && SaveLocked(projectPath, notes))
            {
                if (wasUnread)
                    _unreadCache.AddOrUpdate(projectPath, 0, (_, count) => Math.Max(0, count - 1));
            }
        }
    }

    public int GetUnreadCount(string projectPath)
    {
        // Fast path: return cached count if available
        if (_unreadCache.TryGetValue(projectPath, out var cached))
            return cached;

        // Cold start: load from disk and populate cache
        lock (_lock)
        {
            // Re-check: another thread may have populated the cache while we waited
            if (_unreadCache.TryGetValue(projectPath, out cached))
                return cached;

            var notes = TryLoadLocked(projectPath);
            var count = notes.Count(n => !n.IsRead);
            _unreadCache[projectPath] = count;
            return count;
        }
    }

    /// <summary>
    /// Load notes from disk. Returns empty list if file doesn't exist.
    /// Throws on corruption/IO errors so mutation callers don't overwrite corrupt data.
    /// </summary>
    private List<TeamNote> LoadLocked(string projectPath)
    {
        var path = GetFilePath(projectPath);
        if (!File.Exists(path))
            return [];

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<TeamNote>>(json, JsonDefaults.ReadOptions) ?? [];
    }

    /// <summary>
    /// Safe load for read-only callers (Load, GetUnreadCount) — returns empty on any error.
    /// </summary>
    private List<TeamNote> TryLoadLocked(string projectPath)
    {
        try
        {
            return LoadLocked(projectPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"TeamNotes load failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Returns true if save succeeded, false on IO failure.
    /// Callers should only update _unreadCache when this returns true.
    /// </summary>
    private bool SaveLocked(string projectPath, List<TeamNote> notes)
    {
        try
        {
            Directory.CreateDirectory(NotesDir);
            var path = GetFilePath(projectPath);
            var tmpPath = path + ".tmp";
            var json = JsonSerializer.Serialize(notes, JsonDefaults.Options);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            // IO errors (OneDrive lock, disk full) are non-fatal but should be visible
            System.Diagnostics.Trace.TraceWarning($"TeamNotes save failed: {ex.Message}");
            return false;
        }
    }

    private static string GetFilePath(string projectPath)
    {
        var normalized = projectPath.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return Path.Combine(NotesDir, $"{hash}.json");
    }
}
