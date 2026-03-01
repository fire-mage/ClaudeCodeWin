namespace ClaudeCodeWin.Services.Highlighting;

public class JavaScriptTokenizer : ILanguageTokenizer
{
    internal static readonly HashSet<string> Keywords =
    [
        "function", "const", "let", "var", "class", "extends", "import", "export",
        "default", "async", "await", "yield", "new", "delete", "typeof", "instanceof",
        "in", "of", "static", "get", "set", "super",
    ];

    internal static readonly HashSet<string> ControlKeywords =
    [
        "if", "else", "for", "while", "do", "switch", "case", "break",
        "continue", "return", "throw", "try", "catch", "finally", "with",
    ];

    internal static readonly HashSet<string> TypeKeywords =
    [
        "string", "number", "boolean", "any", "void", "never", "unknown", "object",
        "symbol", "bigint", "enum", "interface", "type",
        "namespace", "declare", "abstract", "implements", "readonly", "keyof",
        "infer", "as", "is", "satisfies",
    ];

    internal static readonly HashSet<string> LiteralKeywords =
    [
        "true", "false", "null", "undefined", "NaN", "Infinity", "this",
    ];

    public List<SyntaxToken> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var tokens = new List<SyntaxToken>(text.Length / 5);
        int i = 0;
        int len = text.Length;

        // Shebang: #! at very start of file
        if (len >= 2 && text[0] == '#' && text[1] == '!')
        {
            int start = 0;
            i = 2;
            while (i < len && text[i] != '\n') i++;
            tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Comment));
        }

        while (i < len)
        {
            char c = text[i];

            // Whitespace — skip
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // Line comment: //
            if (c == '/' && i + 1 < len && text[i + 1] == '/')
            {
                int start = i;
                i += 2;
                while (i < len && text[i] != '\n') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Comment));
                continue;
            }

            // Block comment: /* ... */
            if (c == '/' && i + 1 < len && text[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i < len)
                {
                    if (text[i] == '*' && i + 1 < len && text[i + 1] == '/')
                    {
                        i += 2;
                        break;
                    }
                    i++;
                }
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Comment));
                continue;
            }

            // Regex literal: /pattern/flags
            // Heuristic: '/' preceded by operator, keyword, or line start (not identifier/number/closing bracket)
            if (c == '/' && IsRegexStart(text, i, tokens))
            {
                int start = i;
                i++; // skip opening /
                bool escaped = false;
                bool inCharClass = false;
                while (i < len && text[i] != '\n')
                {
                    if (escaped) { escaped = false; i++; continue; }
                    if (text[i] == '\\') { escaped = true; i++; continue; }
                    if (text[i] == '[') { inCharClass = true; i++; continue; }
                    if (text[i] == ']') { inCharClass = false; i++; continue; }
                    if (text[i] == '/' && !inCharClass) { i++; break; }
                    i++;
                }
                // Consume flags: g, i, m, s, u, y, d
                while (i < len && char.IsLetter(text[i])) i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Decorator: @identifier
            if (c == '@' && i + 1 < len && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                int start = i;
                i++; // skip @
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Attribute));
                continue;
            }

            // Template literal: `...${expr}...`
            if (c == '`')
            {
                int start = i;
                i = ScanTemplateLiteral(text, i);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Single-quoted string
            if (c == '\'')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '\'' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < len) i++;
                    i++;
                }
                if (i < len && text[i] == '\'') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Double-quoted string
            if (c == '"')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '"' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < len) i++;
                    i++;
                }
                if (i < len && text[i] == '"') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Numbers
            if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(text[i + 1])))
            {
                int start = i;
                i = ScanNumber(text, i);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Number));
                continue;
            }

            // JSX/TSX: <Component ...> or </Component>
            if (c == '<' && IsJsxTagStart(text, i))
            {
                int start = i;
                i++; // skip <
                if (i < len && text[i] == '/') i++; // closing tag
                int tagStart = i;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.' || text[i] == '-'))
                    i++;
                if (i > tagStart)
                    tokens.Add(new SyntaxToken(tagStart, i - tagStart, SyntaxTokenType.TagName));
                // Skip to end of tag (just the tag name token, rest is plain)
                continue;
            }

            // Identifiers and keywords
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                int start = i;
                i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '$'))
                    i++;

                string word = text[start..i];

                if (LiteralKeywords.Contains(word))
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Literal));
                else if (TypeKeywords.Contains(word))
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.TypeKeyword));
                else if (ControlKeywords.Contains(word))
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.ControlKeyword));
                else if (Keywords.Contains(word))
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Keyword));
                else if (IsTypeName(word, text, i))
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.TypeName));
                // else: PlainText — no token needed

                continue;
            }

            // Everything else (operators, punctuation) — skip
            i++;
        }

        return tokens;
    }

    private static int ScanTemplateLiteral(string text, int i)
    {
        int len = text.Length;
        i++; // skip opening `
        while (i < len)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < len) { i += 2; continue; }
            if (c == '`') { i++; return i; }
            if (c == '$' && i + 1 < len && text[i + 1] == '{')
            {
                // Skip ${...} expression (track brace depth, handle strings/nested templates)
                i += 2;
                int depth = 1;
                while (i < len && depth > 0)
                {
                    char ch = text[i];
                    if (ch == '\'' || ch == '"')
                    {
                        char q = ch; i++;
                        while (i < len && text[i] != q && text[i] != '\n')
                        { if (text[i] == '\\' && i + 1 < len) i++; i++; }
                        if (i < len) i++;
                        continue;
                    }
                    if (ch == '`') { i = ScanTemplateLiteral(text, i); continue; }
                    if (ch == '/' && i + 1 < len && text[i + 1] == '/')
                    { while (i < len && text[i] != '\n') i++; continue; }
                    if (ch == '/' && i + 1 < len && text[i + 1] == '*')
                    {
                        i += 2;
                        while (i < len && !(text[i] == '*' && i + 1 < len && text[i + 1] == '/')) i++;
                        if (i < len) i += 2;
                        continue;
                    }
                    if (ch == '{') depth++;
                    else if (ch == '}') depth--;
                    if (depth > 0) i++;
                }
                if (i < len) i++; // skip closing }
                continue;
            }
            i++;
        }
        return i;
    }

    private static int ScanNumber(string text, int i)
    {
        int len = text.Length;

        // Hex: 0x...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'x' || text[i + 1] == 'X'))
        {
            i += 2;
            while (i < len && IsHexDigit(text[i])) i++;
            if (i < len && text[i] == 'n') i++; // BigInt
            return i;
        }

        // Binary: 0b...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'b' || text[i + 1] == 'B'))
        {
            i += 2;
            while (i < len && (text[i] == '0' || text[i] == '1' || text[i] == '_')) i++;
            if (i < len && text[i] == 'n') i++; // BigInt
            return i;
        }

        // Octal: 0o...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'o' || text[i + 1] == 'O'))
        {
            i += 2;
            while (i < len && (text[i] >= '0' && text[i] <= '7' || text[i] == '_')) i++;
            if (i < len && text[i] == 'n') i++; // BigInt
            return i;
        }

        // Decimal / floating point
        while (i < len && (char.IsDigit(text[i]) || text[i] == '_')) i++;

        // Decimal point
        if (i < len && text[i] == '.' && i + 1 < len && char.IsDigit(text[i + 1]))
        {
            i++;
            while (i < len && (char.IsDigit(text[i]) || text[i] == '_')) i++;
        }

        // Exponent
        if (i < len && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;
            if (i < len && (text[i] == '+' || text[i] == '-')) i++;
            while (i < len && (char.IsDigit(text[i]) || text[i] == '_')) i++;
        }

        // BigInt suffix
        if (i < len && text[i] == 'n') i++;

        return i;
    }

    private static bool IsHexDigit(char c)
        => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || c == '_';

    private static bool IsRegexStart(string text, int pos, List<SyntaxToken> tokens)
    {
        // A '/' is a regex start if preceded by an operator, keyword, opening bracket, or line start.
        // If preceded by an identifier, number, or closing bracket — it's division.
        if (pos == 0) return true;

        // Walk back to find the last non-whitespace character
        int j = pos - 1;
        while (j >= 0 && char.IsWhiteSpace(text[j])) j--;
        if (j < 0) return true; // start of text

        char prev = text[j];

        // After closing ), ], or identifier/number — it's division
        if (prev == ')' || prev == ']') return false;
        if (char.IsLetterOrDigit(prev) || prev == '_' || prev == '$')
        {
            // Check if the preceding word is a keyword (return, typeof, etc. can precede regex)
            int wordEnd = j + 1;
            while (j >= 0 && (char.IsLetterOrDigit(text[j]) || text[j] == '_' || text[j] == '$'))
                j--;
            string prevWord = text[(j + 1)..wordEnd];
            return Keywords.Contains(prevWord) || ControlKeywords.Contains(prevWord)
                || TypeKeywords.Contains(prevWord) || LiteralKeywords.Contains(prevWord);
        }

        // After operators, opening brackets, commas, semicolons — it's regex
        return true;
    }

    private static bool IsJsxTagStart(string text, int pos)
    {
        // '<' followed by an uppercase letter (Component), lowercase for intrinsic elements,
        // or '/' for closing tags
        if (pos + 1 >= text.Length) return false;
        char next = text[pos + 1];
        if (next == '/') // closing tag </Component>
        {
            return pos + 2 < text.Length && (char.IsLetter(text[pos + 2]) || text[pos + 2] == '_');
        }
        // Must start with letter — filters out <=, <<, <number
        if (!char.IsLetter(next) && next != '_') return false;

        // Disambiguate from comparison: scan backwards for context
        // Simple heuristic: if preceded by return, =, (, {, [, ,, ;, ?, :, ||, &&, ! — it's JSX
        int j = pos - 1;
        while (j >= 0 && char.IsWhiteSpace(text[j])) j--;
        if (j < 0) return true; // start of file
        char prev = text[j];

        // Check if preceded by a keyword (return, yield, default, case, else)
        if (char.IsLetter(prev) || prev == '_' || prev == '$')
        {
            int wordEnd = j + 1;
            while (j >= 0 && (char.IsLetterOrDigit(text[j]) || text[j] == '_' || text[j] == '$'))
                j--;
            string prevWord = text[(j + 1)..wordEnd];
            return prevWord is "return" or "yield" or "default" or "case" or "else";
        }

        return prev == '=' || prev == '(' || prev == '{' || prev == '[' ||
               prev == ',' || prev == ';' || prev == '?' || prev == ':' ||
               prev == '|' || prev == '&' || prev == '!' || prev == '>' ||
               prev == '\n';
    }

    private static bool IsTypeName(string word, string text, int afterWord)
    {
        // PascalCase heuristic: starts with uppercase, at least 2 chars, has a lowercase letter
        if (word.Length < 2 || !char.IsUpper(word[0]))
            return false;

        // Skip ALL_CAPS constants
        if (word.All(c => char.IsUpper(c) || c == '_' || char.IsDigit(c)))
            return false;

        // Check context: followed by space, <, (, ., {, [, ), ], ,, ;
        if (afterWord < text.Length)
        {
            char next = text[afterWord];
            if (next == ' ' || next == '<' || next == '(' || next == '.' ||
                next == '>' || next == ',' || next == ')' || next == ']' ||
                next == '{' || next == ';' || next == '[' || next == '?')
                return true;
        }

        return false;
    }
}
