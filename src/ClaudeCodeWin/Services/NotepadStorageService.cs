using System.IO;

namespace ClaudeCodeWin.Services;

public class NotepadStorageService
{
    private static readonly string NotepadDir = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin", "notepad"));

    public List<string> GetNoteNames()
    {
        if (!Directory.Exists(NotepadDir))
            return [];

        return Directory.GetFiles(NotepadDir, "*.txt")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string LoadNote(string name)
    {
        var path = GetNotePath(name);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public void SaveNote(string name, string content)
    {
        Directory.CreateDirectory(NotepadDir);
        File.WriteAllText(GetNotePath(name), content);
    }

    public void DeleteNote(string name)
    {
        var path = GetNotePath(name);
        if (File.Exists(path))
            File.Delete(path);
    }

    public string RenameNote(string oldName, string newName)
    {
        var oldPath = GetNotePath(oldName);
        if (!File.Exists(oldPath))
            throw new FileNotFoundException($"Note '{oldName}' does not exist.", oldPath);

        var sanitized = SanitizeFileName(newName);
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "Untitled";

        var actualName = GetUniqueName(sanitized, oldName);
        var newPath = GetNotePath(actualName);

        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            // Case-only rename: use temp file to avoid OS ambiguity
            var tempPath = oldPath + ".tmp";
            File.Move(oldPath, tempPath);
            try
            {
                File.Move(tempPath, newPath);
            }
            catch
            {
                File.Move(tempPath, oldPath);
                throw;
            }
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
        File.WriteAllText(GetNotePath(actualName), "");
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

        return File.Exists(GetNotePath(name));
    }

    private string GetNotePath(string name)
    {
        var path = Path.GetFullPath(Path.Combine(NotepadDir, $"{name}.txt"));
        if (!path.StartsWith(NotepadDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid note name.");
        return path;
    }
}
