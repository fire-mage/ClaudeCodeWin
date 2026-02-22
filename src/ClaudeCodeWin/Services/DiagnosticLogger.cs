using System.IO;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Optional diagnostic logger that writes raw stream-json lines to a daily log file.
/// </summary>
public static class DiagnosticLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeWin", "logs");

    private static string LogPath => Path.Combine(LogDir,
        $"stream-{DateTime.Now:yyyy-MM-dd}.log");

    private static bool _enabled;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value) try { Directory.CreateDirectory(LogDir); } catch { }
        }
    }

    public static void Log(string category, string message)
    {
        if (!_enabled) return;
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}\n"); }
        catch { }
    }
}
