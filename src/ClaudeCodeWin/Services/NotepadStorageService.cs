using System.IO;
using System.Text.Json;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class NotepadStorageService
{
    private static readonly string NotepadDir = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin", "notepad"));

    private static readonly string ImagesDir = Path.Combine(NotepadDir, "images");

    public List<string> GetNoteNames()
    {
        if (!Directory.Exists(NotepadDir))
            return [];

        // Collect both .txt (legacy) and .json note files
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in Directory.GetFiles(NotepadDir, "*.txt"))
            names.Add(Path.GetFileNameWithoutExtension(f));
        foreach (var f in Directory.GetFiles(NotepadDir, "*.json"))
            names.Add(Path.GetFileNameWithoutExtension(f));

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Load note as plain text (legacy compatibility + simple editing).</summary>
    public string LoadNote(string name)
    {
        var blocks = LoadNoteBlocks(name);
        return string.Join("\n", blocks.Where(b => b.Type == NoteBlockType.Text && b.Text != null).Select(b => b.Text));
    }

    /// <summary>Load note as structured blocks (text + images).</summary>
    public List<NoteBlock> LoadNoteBlocks(string name)
    {
        var jsonPath = GetNotePath(name, ".json");
        if (File.Exists(jsonPath))
        {
            try
            {
                var json = File.ReadAllText(jsonPath);
                var blocks = JsonSerializer.Deserialize<List<NoteBlock>>(json);
                if (blocks is { Count: > 0 }) return blocks;
            }
            catch { /* fall through to empty */ }
        }

        var txtPath = GetNotePath(name, ".txt");
        if (File.Exists(txtPath))
        {
            var text = File.ReadAllText(txtPath);
            return [NoteBlock.CreateText(text)];
        }

        return [NoteBlock.CreateText("")];
    }

    /// <summary>Save note as plain text (creates .json with single text block).</summary>
    public void SaveNote(string name, string content)
    {
        SaveNoteBlocks(name, [NoteBlock.CreateText(content)]);
    }

    /// <summary>Save note as structured blocks. Images are already cached.</summary>
    public void SaveNoteBlocks(string name, List<NoteBlock> blocks)
    {
        Directory.CreateDirectory(NotepadDir);
        var jsonPath = GetNotePath(name, ".json");

        // Clean up orphaned cached images from previous version of this note
        if (File.Exists(jsonPath))
        {
            try
            {
                var oldBlocks = JsonSerializer.Deserialize<List<NoteBlock>>(File.ReadAllText(jsonPath));
                var newImageFiles = new HashSet<string>(
                    blocks.Where(b => b.ImageFile != null).Select(b => b.ImageFile!),
                    StringComparer.OrdinalIgnoreCase);
                if (oldBlocks != null)
                {
                    foreach (var old in oldBlocks.Where(b => b.Type == NoteBlockType.Image && b.ImageFile != null))
                    {
                        if (!newImageFiles.Contains(old.ImageFile!))
                        {
                            var imgPath = Path.Combine(ImagesDir, old.ImageFile!);
                            try { if (File.Exists(imgPath)) File.Delete(imgPath); } catch { }
                        }
                    }
                }
            }
            catch { /* best-effort cleanup */ }
        }

        var json = JsonSerializer.Serialize(blocks, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);

        // Remove legacy .txt if .json now exists
        var txtPath = GetNotePath(name, ".txt");
        if (File.Exists(txtPath))
            File.Delete(txtPath);
    }

    /// <summary>Cache an image file into notepad/images/ and return the cached filename.</summary>
    public string CacheImage(string sourceFilePath)
    {
        var fi = new FileInfo(sourceFilePath);
        if (fi.Length > 50 * 1024 * 1024)
            throw new InvalidOperationException("Image file is too large (max 50 MB).");

        Directory.CreateDirectory(ImagesDir);

        var ext = Path.GetExtension(sourceFilePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";

        var cachedName = $"{Guid.NewGuid():N}{ext}";
        var destPath = Path.Combine(ImagesDir, cachedName);
        File.Copy(sourceFilePath, destPath, overwrite: true);
        return cachedName;
    }

    /// <summary>Get full path to a cached image file.</summary>
    public string GetImagePath(string cachedFileName)
    {
        var path = Path.GetFullPath(Path.Combine(ImagesDir, cachedFileName));
        if (!path.StartsWith(ImagesDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid image file name.");
        return path;
    }

    public void DeleteNote(string name)
    {
        // Load blocks to find image files to clean up
        var blocks = LoadNoteBlocks(name);
        foreach (var block in blocks.Where(b => b.Type == NoteBlockType.Image && b.ImageFile != null))
        {
            var imgPath = Path.Combine(ImagesDir, block.ImageFile!);
            try { if (File.Exists(imgPath)) File.Delete(imgPath); } catch { }
        }

        var jsonPath = GetNotePath(name, ".json");
        if (File.Exists(jsonPath)) File.Delete(jsonPath);

        var txtPath = GetNotePath(name, ".txt");
        if (File.Exists(txtPath)) File.Delete(txtPath);
    }

    public string RenameNote(string oldName, string newName)
    {
        string? oldPath = null;
        string oldExt = ".json";
        var jsonPath = GetNotePath(oldName, ".json");
        var txtPath = GetNotePath(oldName, ".txt");

        if (File.Exists(jsonPath))
            oldPath = jsonPath;
        else if (File.Exists(txtPath))
        {
            oldPath = txtPath;
            oldExt = ".txt";
        }

        if (oldPath == null)
            throw new FileNotFoundException($"Note '{oldName}' does not exist.");

        var sanitized = SanitizeFileName(newName);
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "Untitled";

        var actualName = GetUniqueName(sanitized, oldName);
        var newPath = GetNotePath(actualName, oldExt);

        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            var tempPath = oldPath + ".tmp";
            File.Move(oldPath, tempPath);
            try { File.Move(tempPath, newPath); }
            catch { File.Move(tempPath, oldPath); throw; }
        }
        else
        {
            File.Move(oldPath, newPath);
        }

        return actualName;
    }

    public string CreateNote(string baseName = "New Note")
    {
        Directory.CreateDirectory(NotepadDir);

        var sanitized = SanitizeFileName(baseName);
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "New Note";

        var actualName = GetUniqueName(sanitized);
        File.WriteAllText(GetNotePath(actualName, ".json"), "[]");
        return actualName;
    }

    /// <summary>Create a note and save blocks in a single write (avoids writing empty [] first).</summary>
    public string CreateAndSaveNote(string baseName, List<NoteBlock> blocks)
    {
        Directory.CreateDirectory(NotepadDir);

        var sanitized = SanitizeFileName(baseName);
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "New Note";

        var actualName = GetUniqueName(sanitized);
        var json = JsonSerializer.Serialize(blocks, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(GetNotePath(actualName, ".json"), json);
        return actualName;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };

    private string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        if (sanitized.Length > 180)
            sanitized = sanitized[..180].TrimEnd('.');
        var stem = sanitized.Split('.')[0];
        if (ReservedNames.Contains(stem))
            sanitized = $"_{sanitized}";
        return sanitized;
    }

    private string GetUniqueName(string baseName, string? excludeName = null)
    {
        if (!NoteExists(baseName, excludeName))
            return baseName;

        for (int i = 2; i < 10000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!NoteExists(candidate, excludeName))
                return candidate;
        }

        throw new InvalidOperationException($"Cannot find unique name for '{baseName}'");
    }

    private bool NoteExists(string name, string? excludeName = null)
    {
        if (string.Equals(name, excludeName, StringComparison.OrdinalIgnoreCase))
            return false;

        return File.Exists(GetNotePath(name, ".json")) || File.Exists(GetNotePath(name, ".txt"));
    }

    private string GetNotePath(string name, string extension)
    {
        var path = Path.GetFullPath(Path.Combine(NotepadDir, $"{name}{extension}"));
        if (!path.StartsWith(NotepadDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid note name.");
        return path;
    }
}
