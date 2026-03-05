namespace ClaudeCodeWin.Services.Highlighting;

public class CssTokenizer : ILanguageTokenizer
{
    private enum State { Selector, PropertyName, PropertyValue, Comment, AtRule }

    // Common CSS value keywords
    private static readonly HashSet<string> ValueKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "inherit", "initial", "unset", "revert", "none", "auto", "normal",
        "block", "inline", "inline-block", "flex", "grid", "inline-flex", "inline-grid", "contents",
        "absolute", "relative", "fixed", "sticky", "static",
        "hidden", "visible", "scroll", "collapse",
        "solid", "dashed", "dotted", "double", "groove", "ridge", "inset", "outset",
        "bold", "bolder", "lighter", "italic", "oblique",
        "left", "right", "center", "justify", "start", "end",
        "top", "bottom", "middle", "baseline",
        "nowrap", "wrap", "wrap-reverse",
        "row", "row-reverse", "column", "column-reverse",
        "stretch", "space-between", "space-around", "space-evenly",
        "pointer", "default", "text", "move", "not-allowed", "crosshair",
        "transparent", "currentColor",
        "ease", "ease-in", "ease-out", "ease-in-out", "linear",
        "forwards", "backwards", "both", "alternate", "alternate-reverse",
        "infinite", "running", "paused",
        "border-box", "content-box", "padding-box",
        "cover", "contain", "no-repeat", "repeat", "repeat-x", "repeat-y",
        "uppercase", "lowercase", "capitalize",
        "underline", "overline", "line-through",
        "pre", "pre-wrap", "pre-line", "break-spaces",
        "table", "table-row", "table-cell", "list-item",
    };

    public List<SyntaxToken> Tokenize(string text)
    {
        return TokenizeBlock(text, 0);
    }

    /// <summary>Tokenize CSS text with a base offset (for embedding in HTML).</summary>
    public List<SyntaxToken> TokenizeBlock(string text, int offset)
    {
        // Bug fix: missing null/empty check caused NullReferenceException
        if (string.IsNullOrEmpty(text))
            return [];

        var tokens = new List<SyntaxToken>(text.Length / 6);
        int i = 0;
        int len = text.Length;
        var state = State.Selector;
        int braceDepth = 0;

        while (i < len)
        {
            char c = text[i];

            // Block comments anywhere
            if (c == '/' && i + 1 < len && text[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < len && !(text[i] == '*' && text[i + 1] == '/'))
                    i++;
                if (i + 1 < len) i += 2; else i = len;
                tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.Comment));
                continue;
            }

            switch (state)
            {
                case State.Selector:
                    if (c == '@')
                    {
                        state = State.AtRule;
                        int start = i;
                        i++;
                        while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '-'))
                            i++;
                        tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.Preprocessor));
                        continue;
                    }
                    if (c == '{')
                    {
                        braceDepth++;
                        state = State.PropertyName;
                        i++;
                        continue;
                    }
                    if (c == '.' || c == '#')
                    {
                        // Class or ID selector
                        int start = i;
                        i++;
                        while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == '_'))
                            i++;
                        tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.TypeKeyword));
                        continue;
                    }
                    if (c == ':')
                    {
                        // Pseudo-class/element
                        int start = i;
                        i++;
                        if (i < len && text[i] == ':') i++; // ::
                        while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '-'))
                            i++;
                        tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.ControlKeyword));
                        continue;
                    }
                    if (c == '[')
                    {
                        // Attribute selector — skip to ]
                        int start = i;
                        i++;
                        while (i < len && text[i] != ']')
                        {
                            if (text[i] == '"' || text[i] == '\'')
                            {
                                char q = text[i];
                                i++;
                                while (i < len && text[i] != q) i++;
                                if (i < len) i++;
                            }
                            else i++;
                        }
                        if (i < len) i++; // ]
                        tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.TypeKeyword));
                        continue;
                    }
                    if (c == '"' || c == '\'')
                    {
                        ScanString(text, ref i, tokens, offset);
                        continue;
                    }
                    if (c == '}')
                    {
                        braceDepth--;
                        i++;
                        continue;
                    }
                    if (char.IsLetter(c) || c == '*')
                    {
                        // Tag selector or universal
                        int start = i;
                        i++;
                        while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == '_'))
                            i++;
                        tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.TypeName));
                        continue;
                    }
                    i++;
                    break;

                case State.AtRule:
                    if (c == '{')
                    {
                        braceDepth++;
                        // At-rule block: could be nested (@media) or declaration (@font-face)
                        // Peek back to determine — for simplicity, switch to selector mode inside
                        state = State.Selector;
                        i++;
                        continue;
                    }
                    if (c == ';')
                    {
                        state = State.Selector;
                        i++;
                        continue;
                    }
                    if (c == '"' || c == '\'')
                    {
                        ScanString(text, ref i, tokens, offset);
                        continue;
                    }
                    if (char.IsDigit(c) || (c == '-' && i + 1 < len && char.IsDigit(text[i + 1])))
                    {
                        ScanNumber(text, ref i, tokens, offset);
                        continue;
                    }
                    i++;
                    break;

                case State.PropertyName:
                    if (c == '}')
                    {
                        braceDepth--;
                        state = State.Selector;
                        i++;
                        continue;
                    }
                    if (c == '!')
                    {
                        // !important
                        if (i + 10 <= len && text.AsSpan(i, 10).Equals("!important".AsSpan(), StringComparison.OrdinalIgnoreCase))
                        {
                            tokens.Add(new SyntaxToken(offset + i, 10, SyntaxTokenType.ControlKeyword));
                            i += 10;
                            continue;
                        }
                        i++;
                        continue;
                    }
                    if (char.IsWhiteSpace(c) || c == ';')
                    {
                        i++;
                        continue;
                    }
                    if (char.IsLetter(c) || c == '-' || c == '_')
                    {
                        int start = i;
                        i++;
                        while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == '_'))
                            i++;
                        // Next non-space char determines if this is property or value
                        int peek = i;
                        while (peek < len && char.IsWhiteSpace(text[peek])) peek++;
                        if (peek < len && text[peek] == ':')
                        {
                            tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.Attribute));
                            // Skip the colon
                            i = peek + 1;
                            state = State.PropertyValue;
                        }
                        else
                        {
                            // Probably a nested selector or something else — treat as TypeName
                            tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.TypeName));
                        }
                        continue;
                    }
                    i++;
                    break;

                case State.PropertyValue:
                    if (c == ';')
                    {
                        state = State.PropertyName;
                        i++;
                        continue;
                    }
                    if (c == '}')
                    {
                        braceDepth--;
                        state = State.Selector;
                        i++;
                        continue;
                    }
                    if (c == '!')
                    {
                        if (i + 10 <= len && text.AsSpan(i, 10).Equals("!important".AsSpan(), StringComparison.OrdinalIgnoreCase))
                        {
                            tokens.Add(new SyntaxToken(offset + i, 10, SyntaxTokenType.ControlKeyword));
                            i += 10;
                            continue;
                        }
                        i++;
                        continue;
                    }
                    if (c == '"' || c == '\'')
                    {
                        ScanString(text, ref i, tokens, offset);
                        continue;
                    }
                    if (c == '#')
                    {
                        // Color hex
                        int start = i;
                        i++;
                        while (i < len && IsHexChar(text[i])) i++;
                        tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.Number));
                        continue;
                    }
                    if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(text[i + 1]))
                        || (c == '-' && i + 1 < len && (char.IsDigit(text[i + 1]) || text[i + 1] == '.')))
                    {
                        ScanNumber(text, ref i, tokens, offset);
                        continue;
                    }
                    if (char.IsLetter(c) || c == '-' || c == '_')
                    {
                        int start = i;
                        i++;
                        while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == '_'))
                            i++;
                        var word = text[start..i];
                        // Check if it's a function call (e.g. rgb(), calc())
                        if (i < len && text[i] == '(')
                        {
                            tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.Keyword));
                        }
                        else if (ValueKeywords.Contains(word))
                        {
                            tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.Keyword));
                        }
                        else
                        {
                            tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.PlainText));
                        }
                        continue;
                    }
                    i++;
                    break;
            }
        }

        return tokens;
    }

    private static void ScanString(string text, ref int i, List<SyntaxToken> tokens, int offset)
    {
        char quote = text[i];
        int start = i;
        i++;
        while (i < text.Length)
        {
            if (text[i] == '\\') { i = Math.Min(i + 2, text.Length); continue; }
            if (text[i] == quote) { i++; break; }
            i++;
        }
        tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.String));
    }

    private static void ScanNumber(string text, ref int i, List<SyntaxToken> tokens, int offset)
    {
        int start = i;
        if (text[i] == '-') i++;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
            i++;
        // Unit suffix (px, em, rem, %, vw, vh, etc.)
        while (i < text.Length && (char.IsLetter(text[i]) || text[i] == '%'))
            i++;
        tokens.Add(new SyntaxToken(offset + start, i - start, SyntaxTokenType.Number));
    }

    private static bool IsHexChar(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
