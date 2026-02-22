namespace ClaudeCodeWin.Infrastructure;

public static class PathHelper
{
    public static string NormalizePath(this string path)
        => path.TrimEnd('\\', '/');

    public static bool PathEquals(this string path, string other)
        => string.Equals(path.NormalizePath(), other.NormalizePath(),
            StringComparison.OrdinalIgnoreCase);

    public static bool IsSubPathOf(this string path, string parent)
    {
        var normalizedPath = path.NormalizePath() + "\\";
        var normalizedParent = parent.NormalizePath() + "\\";
        return normalizedPath.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase)
               && !normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }
}
