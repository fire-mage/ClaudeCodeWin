namespace ClaudeCodeWin.Services.Highlighting;

public class CSharpTokenizer : ILanguageTokenizer
{
    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "break", "case", "catch", "checked", "class",
        "const", "continue", "default", "delegate", "do", "enum", "event",
        "explicit", "extern", "finally", "fixed", "for", "foreach", "goto",
        "if", "else", "implicit", "in", "interface", "internal", "is", "lock",
        "namespace", "new", "operator", "out", "override", "params", "partial",
        "private", "protected", "public", "readonly", "ref", "return", "sealed",
        "sizeof", "stackalloc", "static", "struct", "switch", "this", "throw",
        "try", "typeof", "unchecked", "unsafe", "using", "virtual", "void",
        "volatile", "where", "while", "yield", "async", "await", "record",
        "init", "required", "file", "scoped", "global", "with", "get", "set",
        "value", "add", "remove", "when", "and", "or", "not",
    ];

    private static readonly HashSet<string> ControlKeywords =
    [
        "if", "else", "for", "foreach", "while", "do", "switch", "case",
        "break", "continue", "return", "throw", "try", "catch", "finally",
        "goto", "yield", "default", "when",
    ];

    private static readonly HashSet<string> TypeKeywords =
    [
        "bool", "byte", "sbyte", "char", "decimal", "double", "float",
        "int", "uint", "long", "ulong", "nint", "nuint", "object",
        "short", "ushort", "string", "var", "dynamic", "void",
    ];

    private static readonly HashSet<string> LiteralKeywords =
    [
        "true", "false", "null",
    ];

    public List<SyntaxToken> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var tokens = new List<SyntaxToken>(text.Length / 5);
        int i = 0;
        int len = text.Length;

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

            // Preprocessor directive: # at start of line (or after whitespace on line)
            if (c == '#' && IsLineStart(text, i))
            {
                int start = i;
                i++;
                while (i < len && text[i] != '\n') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
                continue;
            }

            // Strings
            if (c == '"' || c == '\'' ||
                (c == '@' && i + 1 < len && (text[i + 1] == '"' || (text[i + 1] == '$' && i + 2 < len && text[i + 2] == '"'))) ||
                (c == '$' && i + 1 < len && (text[i + 1] == '"' || (text[i + 1] == '@' && i + 2 < len && text[i + 2] == '"'))))
            {
                int start = i;
                i = ScanString(text, i);
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

            // Identifiers and keywords
            if (char.IsLetter(c) || c == '_' || c == '@' && i + 1 < len && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                int start = i;
                if (c == '@') i++; // verbatim identifier prefix
                i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;

                string word = text[start..i];
                bool isVerbatimIdentifier = word.StartsWith('@');

                // Verbatim identifiers (@if, @class, etc.) are NOT keywords
                if (!isVerbatimIdentifier)
                {
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
                }

                continue;
            }

            // Everything else (operators, punctuation) — skip, rendered as plain text
            i++;
        }

        return tokens;
    }

    private static int ScanString(string text, int i)
    {
        int len = text.Length;

        // Skip $ and/or @ prefix to check for raw string literals (""")
        int prefixLen = 0;
        int quoteStart = i;
        if (quoteStart < len && text[quoteStart] == '$') { prefixLen++; quoteStart++; }
        if (quoteStart < len && text[quoteStart] == '@') { prefixLen++; quoteStart++; }
        // Also handle @$ (reversed order)
        if (prefixLen == 0 && i < len && text[i] == '@')
        {
            quoteStart = i + 1;
            if (quoteStart < len && text[quoteStart] == '$') { prefixLen = 2; quoteStart++; }
            else { prefixLen = 0; quoteStart = i; } // reset, not @$
        }

        // Raw string literals: """ or more (with optional $/@/$ prefix)
        if (quoteStart + 2 < len && text[quoteStart] == '"' && text[quoteStart + 1] == '"' && text[quoteStart + 2] == '"')
        {
            int quoteCount = 0;
            int j = quoteStart;
            while (j < len && text[j] == '"') { quoteCount++; j++; }
            // Find matching closing quotes (same count)
            while (j < len)
            {
                if (text[j] == '"')
                {
                    int closingCount = 0;
                    int k = j;
                    while (k < len && text[k] == '"') { closingCount++; k++; }
                    if (closingCount >= quoteCount)
                        return k;
                    j = k;
                }
                else
                {
                    j++;
                }
            }
            return j;
        }

        // $@ or @$ string
        if ((text[i] == '$' && i + 1 < len && text[i + 1] == '@') ||
            (text[i] == '@' && i + 1 < len && text[i + 1] == '$'))
        {
            i += 3; // skip $@" or @$"
            return ScanVerbatimStringBody(text, i);
        }

        // @"verbatim string"
        if (text[i] == '@' && i + 1 < len && text[i + 1] == '"')
        {
            i += 2; // skip @"
            return ScanVerbatimStringBody(text, i);
        }

        // $"interpolated string"
        if (text[i] == '$' && i + 1 < len && text[i + 1] == '"')
        {
            i += 2; // skip $"
            return ScanInterpolatedStringBody(text, i);
        }

        // 'char literal'
        if (text[i] == '\'')
        {
            i++; // skip opening '
            while (i < len && text[i] != '\'' && text[i] != '\n')
            {
                if (text[i] == '\\' && i + 1 < len) i++; // skip escape
                i++;
            }
            if (i < len && text[i] == '\'') i++; // skip closing '
            return i;
        }

        // Regular "string"
        i++; // skip opening "
        while (i < len && text[i] != '"' && text[i] != '\n')
        {
            if (text[i] == '\\' && i + 1 < len) i++; // skip escape
            i++;
        }
        if (i < len && text[i] == '"') i++; // skip closing "
        return i;
    }

    private static int ScanVerbatimStringBody(string text, int i)
    {
        int len = text.Length;
        while (i < len)
        {
            if (text[i] == '"')
            {
                if (i + 1 < len && text[i + 1] == '"')
                    i += 2; // escaped "" in verbatim
                else
                    return i + 1; // closing "
            }
            else
            {
                i++;
            }
        }
        return i;
    }

    private static int ScanInterpolatedStringBody(string text, int i)
    {
        int len = text.Length;
        int braceDepth = 0;
        while (i < len)
        {
            char c = text[i];
            if (c == '{')
            {
                if (i + 1 < len && text[i + 1] == '{')
                    i += 2; // literal {{
                else
                {
                    braceDepth++;
                    i++;
                }
            }
            else if (c == '}')
            {
                if (braceDepth > 0)
                {
                    braceDepth--;
                    i++;
                }
                else if (i + 1 < len && text[i + 1] == '}')
                    i += 2; // literal }}
                else
                    i++;
            }
            else if (c == '"' && braceDepth == 0)
            {
                return i + 1; // closing "
            }
            else if (c == '\\' && i + 1 < len)
            {
                i += 2; // escape
            }
            else if (c == '\n' && braceDepth == 0)
            {
                return i; // unterminated
            }
            else
            {
                i++;
            }
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
            i = SkipNumericSuffix(text, i);
            return i;
        }

        // Binary: 0b...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'b' || text[i + 1] == 'B'))
        {
            i += 2;
            while (i < len && (text[i] == '0' || text[i] == '1' || text[i] == '_')) i++;
            i = SkipNumericSuffix(text, i);
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

        i = SkipNumericSuffix(text, i);
        return i;
    }

    private static int SkipNumericSuffix(string text, int i)
    {
        int len = text.Length;
        // Suffixes: f, d, m, u, l, ul, lu, etc.
        if (i < len)
        {
            char s = char.ToLower(text[i]);
            if (s == 'f' || s == 'd' || s == 'm')
                return i + 1;
            if (s == 'u')
            {
                i++;
                if (i < len && char.ToLower(text[i]) == 'l') i++;
                return i;
            }
            if (s == 'l')
            {
                i++;
                if (i < len && char.ToLower(text[i]) == 'u') i++;
                return i;
            }
        }
        return i;
    }

    private static bool IsHexDigit(char c)
        => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || c == '_';

    private static bool IsLineStart(string text, int pos)
    {
        // True if pos is at start of text or preceded only by whitespace on this line
        for (int j = pos - 1; j >= 0; j--)
        {
            if (text[j] == '\n') return true;
            if (!char.IsWhiteSpace(text[j])) return false;
        }
        return true; // start of text
    }

    private static bool IsTypeName(string word, string text, int afterWord)
    {
        // Must start with uppercase, at least 2 chars, not all-uppercase short word
        if (word.Length < 2 || !char.IsUpper(word[0]))
            return false;

        // Skip ALL_CAPS constants (e.g., MAX_VALUE) — those are typically constants
        if (word.Length >= 2 && word.All(c => char.IsUpper(c) || c == '_' || char.IsDigit(c)))
            return false;

        // Check context: followed by space, <, ?, [, (, ., or is at end
        if (afterWord < text.Length)
        {
            char next = text[afterWord];
            if (next == ' ' || next == '<' || next == '?' || next == '[' ||
                next == '(' || next == '.' || next == '>' || next == ',' ||
                next == ')' || next == ']' || next == '{' || next == ';')
                return true;
        }

        return false;
    }
}
