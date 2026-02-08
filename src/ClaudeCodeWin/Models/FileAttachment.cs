namespace ClaudeCodeWin.Models;

public class FileAttachment
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public bool IsScreenshot { get; set; }
}
