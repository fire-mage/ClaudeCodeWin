namespace ClaudeCodeWin.Services.Highlighting;

public class CppTokenizer : ILanguageTokenizer
{
    // C keywords + C++ additions
    internal static readonly HashSet<string> Keywords =
    [
        // C keywords
        "auto", "break", "case", "const", "continue", "default", "do", "else",
        "enum", "extern", "for", "goto", "if", "inline", "register", "restrict",
        "return", "sizeof", "static", "struct", "switch", "typedef", "union",
        "volatile", "while",
        // C++ additions
        "class", "namespace", "template", "typename", "virtual", "override",
        "final", "explicit", "friend", "mutable", "operator", "new", "delete",
        "this", "throw", "try", "catch", "using", "constexpr", "consteval",
        "constinit", "decltype", "noexcept", "static_assert", "concept",
        "requires", "co_await", "co_return", "co_yield", "export", "module",
        "import",
    ];

    internal static readonly HashSet<string> ControlKeywords =
    [
        "if", "else", "for", "while", "do", "switch", "case", "default",
        "break", "continue", "return", "goto", "throw", "try", "catch",
    ];

    internal static readonly HashSet<string> TypeKeywords =
    [
        // C types
        "int", "char", "float", "double", "void", "bool", "long", "short",
        "unsigned", "signed", "wchar_t", "size_t", "auto",
        // C++ STL types
        "string", "vector", "map", "set", "array", "shared_ptr", "unique_ptr",
        "optional", "variant", "tuple",
    ];

    internal static readonly HashSet<string> LiteralKeywords =
    [
        "true", "false", "nullptr", "NULL",
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

            // Preprocessor directive: # at start of line
            if (c == '#' && IsLineStart(text, i))
            {
                int start = i;
                i++;
                // Skip whitespace after #
                while (i < len && text[i] == ' ') i++;

                // Check if this is #include with <header>
                int directiveStart = i;
                while (i < len && char.IsLetter(text[i])) i++;
                string directive = text[directiveStart..i];

                if (directive == "include")
                {
                    // Highlight #include as Preprocessor
                    int prepEnd = i;
                    // Skip whitespace
                    while (i < len && text[i] == ' ') i++;

                    if (i < len && text[i] == '<')
                    {
                        // #include <header.h> — highlight #include as Preprocessor, <path> as String
                        tokens.Add(new SyntaxToken(start, prepEnd - start, SyntaxTokenType.Preprocessor));
                        int angleStart = i;
                        i++; // skip <
                        while (i < len && text[i] != '>' && text[i] != '\n') i++;
                        if (i < len && text[i] == '>') i++;
                        tokens.Add(new SyntaxToken(angleStart, i - angleStart, SyntaxTokenType.String));
                        // Rest of line (if any)
                        while (i < len && text[i] != '\n') i++;
                        continue;
                    }
                    else if (i < len && text[i] == '"')
                    {
                        // #include "file.h" — highlight #include as Preprocessor, "file" as String
                        tokens.Add(new SyntaxToken(start, prepEnd - start, SyntaxTokenType.Preprocessor));
                        int strStart = i;
                        i++; // skip "
                        while (i < len && text[i] != '"' && text[i] != '\n') i++;
                        if (i < len && text[i] == '"') i++;
                        tokens.Add(new SyntaxToken(strStart, i - strStart, SyntaxTokenType.String));
                        while (i < len && text[i] != '\n') i++;
                        continue;
                    }
                }

                // Other preprocessor directives — highlight full line
                while (i < len && text[i] != '\n')
                {
                    // Handle line continuation with backslash
                    if (text[i] == '\\' && i + 1 < len && text[i + 1] == '\n')
                    {
                        i += 2; // skip \<newline> and continue
                        continue;
                    }
                    i++;
                }
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
                continue;
            }

            // C++ raw strings: R"delim(...)delim" and prefixed: LR", uR", UR", u8R"
            {
                int rPos = -1;
                if (c == 'R' && i + 1 < len && text[i + 1] == '"')
                    rPos = i;
                else if ((c == 'L' || c == 'u' || c == 'U') && i + 1 < len)
                {
                    int p = i + 1;
                    if (c == 'u' && p < len && text[p] == '8') p++;
                    if (p + 1 < len && text[p] == 'R' && text[p + 1] == '"')
                        rPos = p;
                }
                if (rPos >= 0)
                {
                    int start = i;
                    i = ScanRawString(text, rPos);
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                    continue;
                }
            }

            // Double-quoted string
            if (c == '"')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '"' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < len) i++; // skip escape
                    i++;
                }
                if (i < len && text[i] == '"') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Char literal: 'x' or '\n'
            if (c == '\'')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '\'' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < len) i++; // skip escape
                    i++;
                }
                if (i < len && text[i] == '\'') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Numbers: hex (0x), octal (0), binary (0b), decimal, floating, suffixes
            if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(text[i + 1])))
            {
                int start = i;
                i = ScanNumber(text, i);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Number));
                continue;
            }

            // Identifiers and keywords
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
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

    /// <summary>Scans a raw string starting at R" (rPos points to 'R'). Returns position after closing delimiter.</summary>
    private static int ScanRawString(string text, int rPos)
    {
        int len = text.Length;
        int i = rPos + 2; // skip R"
        // Read delimiter (can be empty)
        int delimStart = i;
        while (i < len && text[i] != '(' && text[i] != '\n' && text[i] != '"') i++;
        string delim = text[delimStart..i];
        if (i < len && text[i] == '(')
        {
            i++; // skip (
            string closing = ")" + delim + "\"";
            while (i < len)
            {
                if (text[i] == ')' && i + closing.Length <= len
                    && text.AsSpan(i, closing.Length).SequenceEqual(closing))
                {
                    i += closing.Length;
                    break;
                }
                i++;
            }
        }
        else
        {
            // Malformed raw string — scan to end of line
            while (i < len && text[i] != '\n') i++;
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
            while (i < len && (text[i] == '0' || text[i] == '1' || text[i] == '\'')) i++;
            i = SkipNumericSuffix(text, i);
            return i;
        }

        // Octal: 0...
        if (text[i] == '0' && i + 1 < len && char.IsDigit(text[i + 1]))
        {
            i++;
            while (i < len && ((text[i] >= '0' && text[i] <= '7') || text[i] == '\'')) i++;
            i = SkipNumericSuffix(text, i);
            return i;
        }

        // Decimal / floating point (with C++ digit separator ')
        while (i < len && (char.IsDigit(text[i]) || text[i] == '\'')) i++;

        // Decimal point
        if (i < len && text[i] == '.' && i + 1 < len && char.IsDigit(text[i + 1]))
        {
            i++;
            while (i < len && (char.IsDigit(text[i]) || text[i] == '\'')) i++;
        }

        // Exponent
        if (i < len && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;
            if (i < len && (text[i] == '+' || text[i] == '-')) i++;
            while (i < len && (char.IsDigit(text[i]) || text[i] == '\'')) i++;
        }

        i = SkipNumericSuffix(text, i);
        return i;
    }

    private static int SkipNumericSuffix(string text, int i)
    {
        int len = text.Length;
        // C/C++ suffixes: u, l, ll, ul, ull, f, ULL, etc. (case-insensitive combinations)
        if (i >= len) return i;

        char s = char.ToLower(text[i]);
        if (s == 'f')
            return i + 1;
        if (s == 'u')
        {
            i++;
            if (i < len && char.ToLower(text[i]) == 'l')
            {
                i++;
                if (i < len && char.ToLower(text[i]) == 'l') i++;
            }
            return i;
        }
        if (s == 'l')
        {
            i++;
            if (i < len && char.ToLower(text[i]) == 'l') i++;
            if (i < len && char.ToLower(text[i]) == 'u') i++;
            return i;
        }
        return i;
    }

    private static bool IsHexDigit(char c)
        => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || c == '\'';

    private static bool IsLineStart(string text, int pos)
    {
        for (int j = pos - 1; j >= 0; j--)
        {
            if (text[j] == '\n') return true;
            if (!char.IsWhiteSpace(text[j])) return false;
        }
        return true; // start of text
    }

    private static bool IsTypeName(string word, string text, int afterWord)
    {
        if (word.Length < 2 || !char.IsUpper(word[0]))
            return false;

        // Skip ALL_CAPS constants (e.g., MAX_VALUE)
        if (word.All(c => char.IsUpper(c) || c == '_' || char.IsDigit(c)))
            return false;

        if (afterWord < text.Length)
        {
            char next = text[afterWord];
            if (next == ' ' || next == '<' || next == '?' || next == '[' ||
                next == '(' || next == '.' || next == '>' || next == ',' ||
                next == ')' || next == ']' || next == '{' || next == ';' ||
                next == '*' || next == '&' || next == ':')
                return true;
        }

        return false;
    }
}
