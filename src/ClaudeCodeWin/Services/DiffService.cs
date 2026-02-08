namespace ClaudeCodeWin.Services;

public enum DiffLineType { Context, Added, Removed }

public record DiffLine(DiffLineType Type, string Text, int? OldLineNumber, int? NewLineNumber);

public static class DiffService
{
    public static List<DiffLine> ComputeDiff(string? oldText, string? newText)
    {
        var oldLines = (oldText ?? "").Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var newLines = (newText ?? "").Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // Handle trivial cases
        if (oldLines.Length == 1 && oldLines[0] == "" && newLines.Length > 0)
            return newLines.Select((l, i) => new DiffLine(DiffLineType.Added, l, null, i + 1)).ToList();

        if (newLines.Length == 1 && newLines[0] == "" && oldLines.Length > 0)
            return oldLines.Select((l, i) => new DiffLine(DiffLineType.Removed, l, i + 1, null)).ToList();

        // Strip common prefix
        int prefixLen = 0;
        while (prefixLen < oldLines.Length && prefixLen < newLines.Length
               && oldLines[prefixLen] == newLines[prefixLen])
            prefixLen++;

        // Strip common suffix
        int suffixLen = 0;
        while (suffixLen < oldLines.Length - prefixLen && suffixLen < newLines.Length - prefixLen
               && oldLines[oldLines.Length - 1 - suffixLen] == newLines[newLines.Length - 1 - suffixLen])
            suffixLen++;

        var oldMiddle = oldLines[prefixLen..(oldLines.Length - suffixLen)];
        var newMiddle = newLines[prefixLen..(newLines.Length - suffixLen)];

        var result = new List<DiffLine>();

        // Prefix context
        for (int i = 0; i < prefixLen; i++)
            result.Add(new DiffLine(DiffLineType.Context, oldLines[i], i + 1, i + 1));

        // Diff the middle section
        if (oldMiddle.Length <= 3000 && newMiddle.Length <= 3000)
        {
            result.AddRange(LcsDiff(oldMiddle, newMiddle, prefixLen));
        }
        else
        {
            // Too large — show all old as removed, all new as added
            for (int i = 0; i < oldMiddle.Length; i++)
                result.Add(new DiffLine(DiffLineType.Removed, oldMiddle[i], prefixLen + i + 1, null));
            for (int i = 0; i < newMiddle.Length; i++)
                result.Add(new DiffLine(DiffLineType.Added, newMiddle[i], null, prefixLen + i + 1));
        }

        // Suffix context
        for (int i = 0; i < suffixLen; i++)
        {
            var oldIdx = oldLines.Length - suffixLen + i;
            var newIdx = newLines.Length - suffixLen + i;
            result.Add(new DiffLine(DiffLineType.Context, oldLines[oldIdx], oldIdx + 1, newIdx + 1));
        }

        return result;
    }

    private static List<DiffLine> LcsDiff(string[] oldLines, string[] newLines, int offset)
    {
        int m = oldLines.Length, n = newLines.Length;
        var dp = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                dp[i, j] = oldLines[i - 1] == newLines[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        // Backtrack
        var stack = new Stack<DiffLine>();
        int ii = m, jj = n;

        while (ii > 0 || jj > 0)
        {
            if (ii > 0 && jj > 0 && oldLines[ii - 1] == newLines[jj - 1])
            {
                stack.Push(new DiffLine(DiffLineType.Context, oldLines[ii - 1],
                    offset + ii, offset + jj));
                ii--;
                jj--;
            }
            else if (jj > 0 && (ii == 0 || dp[ii, jj - 1] >= dp[ii - 1, jj]))
            {
                stack.Push(new DiffLine(DiffLineType.Added, newLines[jj - 1],
                    null, offset + jj));
                jj--;
            }
            else
            {
                stack.Push(new DiffLine(DiffLineType.Removed, oldLines[ii - 1],
                    offset + ii, null));
                ii--;
            }
        }

        return [.. stack];
    }
}
