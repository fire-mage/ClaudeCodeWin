namespace ClaudeCodeWin.Services.Highlighting;

public class HtmlTokenizer : ILanguageTokenizer
{
    private static readonly CssTokenizer CssTokenizerInstance = new();

    public List<SyntaxToken> Tokenize(string text)
    {
        var tokens = new List<SyntaxToken>(text.Length / 8);
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            char c = text[i];

            if (c == '<')
            {
                // Comment: <!-- ... -->
                if (i + 3 < len && text[i + 1] == '!' && text[i + 2] == '-' && text[i + 3] == '-')
                {
                    int start = i;
                    i += 4;
                    while (i + 2 < len && !(text[i] == '-' && text[i + 1] == '-' && text[i + 2] == '>'))
                        i++;
                    if (i + 2 < len) i += 3; else i = len;
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Comment));
                    continue;
                }

                // DOCTYPE: <!DOCTYPE ...>
                if (i + 8 < len && text.AsSpan(i + 1, 8).Equals("!DOCTYPE".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    int start = i;
                    while (i < len && text[i] != '>') i++;
                    if (i < len) i++; // >
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
                    continue;
                }

                // CDATA: <![CDATA[ ... ]]>
                if (i + 8 < len && text.AsSpan(i + 1, 8).Equals("![CDATA[".AsSpan(), StringComparison.Ordinal))
                {
                    int start = i;
                    i += 9;
                    while (i + 2 < len && !(text[i] == ']' && text[i + 1] == ']' && text[i + 2] == '>'))
                        i++;
                    if (i + 2 < len) i += 3; else i = len;
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Comment));
                    continue;
                }

                // Opening/closing tag
                int tagStart = i;
                // '<' or '</'
                bool isClosing = (i + 1 < len && text[i + 1] == '/');
                int punctLen = isClosing ? 2 : 1;
                tokens.Add(new SyntaxToken(i, punctLen, SyntaxTokenType.ControlKeyword));
                i += punctLen;

                // Tag name
                if (i < len && (char.IsLetter(text[i]) || text[i] == '_'))
                {
                    int nameStart = i;
                    i++;
                    while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == '_' || text[i] == ':'))
                        i++;
                    string tagName = text[nameStart..i];
                    tokens.Add(new SyntaxToken(nameStart, i - nameStart, SyntaxTokenType.TagName));

                    // Attributes
                    ParseAttributes(text, ref i, tokens);

                    // Self-closing /> or >
                    if (i < len)
                    {
                        if (text[i] == '/' && i + 1 < len && text[i + 1] == '>')
                        {
                            tokens.Add(new SyntaxToken(i, 2, SyntaxTokenType.ControlKeyword));
                            i += 2;
                        }
                        else if (text[i] == '>')
                        {
                            tokens.Add(new SyntaxToken(i, 1, SyntaxTokenType.ControlKeyword));
                            i++;
                        }
                    }

                    // Handle <style> blocks — delegate CSS content
                    if (!isClosing && tagName.Equals("style", StringComparison.OrdinalIgnoreCase))
                    {
                        int cssStart = i;
                        int cssEnd = FindClosingTag(text, i, "style");
                        if (cssEnd > cssStart)
                        {
                            string cssContent = text[cssStart..cssEnd];
                            var cssTokens = CssTokenizerInstance.TokenizeBlock(cssContent, cssStart);
                            tokens.AddRange(cssTokens);
                            i = cssEnd;
                        }
                    }
                    // Handle <script> — leave content as plain text until </script>
                    else if (!isClosing && tagName.Equals("script", StringComparison.OrdinalIgnoreCase))
                    {
                        int scriptEnd = FindClosingTag(text, i, "script");
                        i = scriptEnd; // skip script content
                    }
                }
                else
                {
                    // Malformed tag — skip
                    if (i < len && text[i] == '>') { i++; }
                }
                continue;
            }

            // HTML entity: &...;
            if (c == '&')
            {
                int start = i;
                i++;
                if (i < len && text[i] == '#')
                {
                    i++;
                    if (i < len && (text[i] == 'x' || text[i] == 'X'))
                    {
                        i++;
                        while (i < len && IsHexChar(text[i])) i++;
                    }
                    else
                    {
                        while (i < len && char.IsDigit(text[i])) i++;
                    }
                }
                else
                {
                    while (i < len && char.IsLetterOrDigit(text[i])) i++;
                }
                if (i < len && text[i] == ';') i++;
                if (i - start > 1) // At least &X
                {
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Literal));
                    continue;
                }
                // Just a lone '&' — skip
                continue;
            }

            // Plain text
            i++;
        }

        return tokens;
    }

    private static void ParseAttributes(string text, ref int i, List<SyntaxToken> tokens)
    {
        int len = text.Length;

        while (i < len)
        {
            // Skip whitespace
            while (i < len && char.IsWhiteSpace(text[i])) i++;

            if (i >= len) break;
            char c = text[i];

            // End of tag
            if (c == '>' || (c == '/' && i + 1 < len && text[i + 1] == '>'))
                break;

            // Attribute name
            if (char.IsLetter(c) || c == '_' || c == ':' || c == '@' || c == '*'
                || c == '[' || c == '(' || c == '#') // Angular/Vue bindings
            {
                int start = i;
                i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == '_'
                    || text[i] == ':' || text[i] == '.' || text[i] == ']' || text[i] == ')'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Attribute));

                // Skip whitespace
                while (i < len && char.IsWhiteSpace(text[i])) i++;

                // = sign
                if (i < len && text[i] == '=')
                {
                    i++;
                    while (i < len && char.IsWhiteSpace(text[i])) i++;

                    // Attribute value
                    if (i < len)
                    {
                        if (text[i] == '"' || text[i] == '\'')
                        {
                            char quote = text[i];
                            int valStart = i;
                            i++;
                            while (i < len && text[i] != quote) i++;
                            if (i < len) i++; // closing quote
                            tokens.Add(new SyntaxToken(valStart, i - valStart, SyntaxTokenType.String));
                        }
                        else
                        {
                            // Unquoted value
                            int valStart = i;
                            while (i < len && !char.IsWhiteSpace(text[i]) && text[i] != '>' && text[i] != '/')
                                i++;
                            tokens.Add(new SyntaxToken(valStart, i - valStart, SyntaxTokenType.String));
                        }
                    }
                }
                continue;
            }

            // Something unexpected — advance
            i++;
        }
    }

    /// <summary>Find the position of &lt;/tagName&gt; starting from pos. Returns position of the start of closing tag text content.</summary>
    private static int FindClosingTag(string text, int pos, string tagName)
    {
        string pattern = "</" + tagName;
        int len = text.Length;
        int i = pos;

        while (i < len)
        {
            int idx = text.IndexOf(pattern, i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return len;

            // Verify it's followed by > (possibly with whitespace)
            int j = idx + pattern.Length;
            while (j < len && char.IsWhiteSpace(text[j])) j++;
            if (j < len && text[j] == '>')
                return idx;

            i = idx + 1;
        }

        return len;
    }

    private static bool IsHexChar(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
