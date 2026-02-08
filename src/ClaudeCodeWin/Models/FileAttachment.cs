using System.IO;

namespace ClaudeCodeWin.Models;

public class FileAttachment
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"
    };

    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public bool IsScreenshot { get; set; }

    public bool IsImage => IsScreenshot || ImageExtensions.Contains(Path.GetExtension(FileName));
}
