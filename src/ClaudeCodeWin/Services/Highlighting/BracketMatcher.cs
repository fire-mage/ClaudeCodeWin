namespace ClaudeCodeWin.Services.Highlighting;

public static class BracketMatcher
{
    private static readonly Dictionary<char, char> OpenToClose = new()
    {
        ['('] = ')', ['['] = ']', ['{'] = '}'
    };

    private static readonly Dictionary<char, char> CloseToOpen = new()
    {
        [')'] = '(', [']'] = '[', ['}'] = '{'
    };

    /// <summary>
    /// Returns the position of the matching bracket, or -1 if not found.
    /// Skips brackets inside strings and comments using the token list.
    /// </summary>
    public static int FindMatch(string text, int position, List<SyntaxToken> tokens)
    {
        if (position < 0 || position >= text.Length)
            return -1;

        char c = text[position];

        // Check if this position is inside a string or comment
        if (IsInsideStringOrComment(position, tokens))
            return -1;

        if (OpenToClose.TryGetValue(c, out char closeChar))
            return ScanForward(text, position, c, closeChar, tokens);

        if (CloseToOpen.TryGetValue(c, out char openChar))
            return ScanBackward(text, position, openChar, c, tokens);

        return -1;
    }

    private static int ScanForward(string text, int position, char open, char close, List<SyntaxToken> tokens)
    {
        int depth = 1;
        for (int i = position + 1; i < text.Length; i++)
        {
            if (IsInsideStringOrComment(i, tokens))
                continue;

            if (text[i] == open) depth++;
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static int ScanBackward(string text, int position, char open, char close, List<SyntaxToken> tokens)
    {
        int depth = 1;
        for (int i = position - 1; i >= 0; i--)
        {
            if (IsInsideStringOrComment(i, tokens))
                continue;

            if (text[i] == close) depth++;
            else if (text[i] == open)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static bool IsInsideStringOrComment(int position, List<SyntaxToken> tokens)
    {
        // Binary search for the token containing this position
        int lo = 0, hi = tokens.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var token = tokens[mid];
            if (position < token.Start)
                hi = mid - 1;
            else if (position >= token.Start + token.Length)
                lo = mid + 1;
            else
            {
                // Position is inside this token
                return token.Type == SyntaxTokenType.String ||
                       token.Type == SyntaxTokenType.Comment;
            }
        }
        return false;
    }
}
