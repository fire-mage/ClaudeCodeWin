namespace ClaudeCodeWin.Services.Highlighting;

public class JavaTokenizer : ILanguageTokenizer
{
    internal static readonly HashSet<string> Keywords =
    [
        "abstract", "assert", "class", "enum", "extends", "final", "implements",
        "import", "instanceof", "interface", "native", "new", "package", "private",
        "protected", "public", "static", "strictfp", "super", "synchronized",
        "this", "throws", "transient", "volatile", "record", "sealed", "permits",
        "non-sealed", "yield", "var", "module", "requires", "exports", "opens",
        "uses", "provides", "with", "to", "open",
    ];

    internal static readonly HashSet<string> ControlKeywords =
    [
        "if", "else", "for", "while", "do", "switch", "case", "default",
        "break", "continue", "return", "throw", "try", "catch", "finally",
    ];

    internal static readonly HashSet<string> TypeKeywords =
    [
        "boolean", "byte", "char", "short", "int", "long", "float", "double",
        "void", "String",
    ];

    internal static readonly HashSet<string> LiteralKeywords =
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

            // Block comment: /* ... */ and Javadoc: /** ... */
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
                // Both regular block comments and Javadoc (/**) use Comment type
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Comment));
                continue;
            }

            // Annotation: @Identifier (e.g. @Override, @SuppressWarnings)
            if (c == '@' && i + 1 < len && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                int start = i;
                i++; // skip @
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
                continue;
            }

            // Text block: """...""" (Java 13+)
            if (c == '"' && i + 2 < len && text[i + 1] == '"' && text[i + 2] == '"')
            {
                int start = i;
                i += 3;
                while (i < len)
                {
                    if (text[i] == '\\' && i + 1 < len)
                    {
                        i += 2;
                        continue;
                    }
                    if (text[i] == '"' && i + 2 < len && text[i + 1] == '"' && text[i + 2] == '"')
                    {
                        i += 3;
                        break;
                    }
                    i++;
                }
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

            // Char literal: 'x' or '\n'
            if (c == '\'')
            {
                int start = i;
                i++;
                if (i < len && text[i] == '\\' && i + 1 < len)
                {
                    i += 2; // skip escape sequence
                    // Unicode escape: \u0000
                    if (i > start + 2 && text[i - 1] == 'u')
                    {
                        while (i < len && IsHexDigit(text[i])) i++;
                    }
                }
                else if (i < len && text[i] != '\'')
                {
                    i++;
                }
                if (i < len && text[i] == '\'') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Numbers: hex (0x), binary (0b), decimal with underscores, suffixes (L, f, d)
            if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(text[i + 1])))
            {
                int start = i;
                i = ScanNumber(text, i);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Number));
                continue;
            }

            // Identifiers and keywords
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                int start = i;
                i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '$'))
                    i++;

                // Special case: "non-sealed" keyword (hyphenated)
                if (i - start == 3 && text[start..(start + 3)] == "non"
                    && i < len && text[i] == '-'
                    && i + 6 < len && text.AsSpan(i + 1, 6).SequenceEqual("sealed"))
                {
                    i += 7; // consume "-sealed"
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Keyword));
                    continue;
                }

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

    private static int ScanNumber(string text, int i)
    {
        int len = text.Length;

        // Hex: 0x...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'x' || text[i + 1] == 'X'))
        {
            i += 2;
            while (i < len && IsHexDigit(text[i])) i++;
            // Long suffix
            if (i < len && (text[i] == 'L' || text[i] == 'l')) i++;
            return i;
        }

        // Binary: 0b...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'b' || text[i + 1] == 'B'))
        {
            i += 2;
            while (i < len && (text[i] == '0' || text[i] == '1' || text[i] == '_')) i++;
            // Long suffix
            if (i < len && (text[i] == 'L' || text[i] == 'l')) i++;
            return i;
        }

        // Decimal / floating point
        while (i < len && (char.IsDigit(text[i]) || text[i] == '_')) i++;

        // Decimal point
        if (i < len && text[i] == '.')
        {
            int peek = i + 1;
            if (peek < len && (char.IsDigit(text[peek]) || text[peek] == '_'))
            {
                i++;
                while (i < len && (char.IsDigit(text[i]) || text[i] == '_')) i++;
            }
        }

        // Exponent
        if (i < len && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;
            if (i < len && (text[i] == '+' || text[i] == '-')) i++;
            while (i < len && (char.IsDigit(text[i]) || text[i] == '_')) i++;
        }

        // Type suffix: L, l, f, F, d, D
        if (i < len && (text[i] == 'L' || text[i] == 'l' ||
                         text[i] == 'f' || text[i] == 'F' ||
                         text[i] == 'd' || text[i] == 'D'))
            i++;

        return i;
    }

    private static bool IsHexDigit(char c)
        => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || c == '_';

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
