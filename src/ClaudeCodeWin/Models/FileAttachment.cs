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

    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    public string FileTypeIcon
    {
        get
        {
            var ext = Path.GetExtension(FileName);
            if (IsScreenshot || ImageExtensions.Contains(ext)) return "\U0001F5BC"; // framed picture
            if (PdfExtensions.Contains(ext)) return "\U0001F4D1"; // bookmark tabs (PDF)
            return "\U0001F4C4"; // page facing up (generic file)
        }
    }
}
