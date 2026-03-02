using System.Text.RegularExpressions;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Detects USER_NOTE: markers in agent output text.
/// Agents use this format to leave non-blocking observations for the user.
/// </summary>
public static partial class TeamNotesDetector
{
    [GeneratedRegex(@"^USER_NOTE:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex NotePattern();

    /// <summary>
    /// Extracts all USER_NOTE messages from agent output text.
    /// Returns a list of trimmed message strings (prefix stripped).
    /// </summary>
    public static List<string> ExtractNotes(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var matches = NotePattern().Matches(text);
        if (matches.Count == 0)
            return [];

        var notes = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            var message = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(message))
                notes.Add(message);
        }

        return notes;
    }
}
