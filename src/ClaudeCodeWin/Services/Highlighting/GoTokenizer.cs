namespace ClaudeCodeWin.Services.Highlighting;

public class GoTokenizer : ILanguageTokenizer
{
    internal static readonly HashSet<string> Keywords =
    [
        "break", "case", "chan", "const", "continue", "default", "defer",
        "else", "fallthrough", "for", "func", "go", "goto", "if", "import",
        "interface", "map", "package", "range", "return", "select", "struct",
        "switch", "type", "var",
    ];

    internal static readonly HashSet<string> ControlKeywords =
    [
        "if", "else", "for", "switch", "case", "default", "break",
        "continue", "return", "goto", "defer", "select", "fallthrough", "range",
    ];

    internal static readonly HashSet<string> TypeKeywords =
    [
        "bool", "byte", "complex64", "complex128", "error", "float32", "float64",
        "int", "int8", "int16", "int32", "int64", "rune", "string",
        "uint", "uint8", "uint16", "uint32", "uint64", "uintptr",
        "any", "comparable",
    ];

    internal static readonly HashSet<string> LiteralKeywords =
    [
        "true", "false", "nil", "iota",
    ];

    internal static readonly HashSet<string> BuiltinFunctions =
    [
        "make", "len", "cap", "new", "append", "copy", "close", "delete",
        "complex", "real", "imag", "panic", "recover", "print", "println",
        "clear", "min", "max",
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

            // Raw string literal: `...` (backtick, multiline, no escapes)
            if (c == '`')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '`') i++;
                if (i < len) i++; // consume closing backtick
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

            // Rune literal: 'x' or '\n'
            if (c == '\'')
            {
                int start = i;
                i++;
                if (i < len && text[i] == '\\' && i + 1 < len)
                {
                    i += 2;
                    // Unicode escape: \u0000 or \U00000000
                    if (i > start + 2 && (text[i - 1] == 'u' || text[i - 1] == 'U'))
                    {
                        while (i < len && IsHexDigitStrict(text[i])) i++;
                    }
                    // Octal escape: \377
                    else if (i > start + 2 && text[i - 1] >= '0' && text[i - 1] <= '7')
                    {
                        while (i < len && text[i] >= '0' && text[i] <= '7') i++;
                    }
                    // Hex byte escape: \xff
                    else if (i > start + 2 && text[i - 1] == 'x')
                    {
                        while (i < len && IsHexDigitStrict(text[i])) i++;
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

            // Numbers: hex (0x), octal (0o), binary (0b), imaginary (5i), underscores
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
                else if (BuiltinFunctions.Contains(word))
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.TypeName));
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

        // Hex: 0x... (including hex float: 0x1.0p10)
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'x' || text[i + 1] == 'X'))
        {
            i += 2;
            while (i < len && IsHexDigit(text[i])) i++;
            // Hex float: decimal point
            if (i < len && text[i] == '.')
            {
                i++;
                while (i < len && IsHexDigit(text[i])) i++;
            }
            // Hex float: p/P exponent (only valid for hex literals)
            if (i < len && (text[i] == 'p' || text[i] == 'P'))
            {
                i++;
                if (i < len && (text[i] == '+' || text[i] == '-')) i++;
                while (i < len && (char.IsDigit(text[i]) || text[i] == '_')) i++;
            }
            // Imaginary suffix
            if (i < len && text[i] == 'i') i++;
            return i;
        }

        // Octal: 0o...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'o' || text[i + 1] == 'O'))
        {
            i += 2;
            while (i < len && ((text[i] >= '0' && text[i] <= '7') || text[i] == '_')) i++;
            if (i < len && text[i] == 'i') i++;
            return i;
        }

        // Binary: 0b...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'b' || text[i + 1] == 'B'))
        {
            i += 2;
            while (i < len && (text[i] == '0' || text[i] == '1' || text[i] == '_')) i++;
            if (i < len && text[i] == 'i') i++;
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

        // Imaginary suffix
        if (i < len && text[i] == 'i') i++;

        return i;
    }

    private static bool IsHexDigit(char c)
        => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || c == '_';

    private static bool IsHexDigitStrict(char c)
        => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsTypeName(string word, string text, int afterWord)
    {
        // PascalCase heuristic: starts with uppercase, at least 2 chars, has a lowercase letter
        if (word.Length < 2 || !char.IsUpper(word[0]))
            return false;

        // Skip ALL_CAPS constants
        if (word.All(c => char.IsUpper(c) || c == '_' || char.IsDigit(c)))
            return false;

        // Check context: followed by space, {, (, ., ), etc.
        if (afterWord < text.Length)
        {
            char next = text[afterWord];
            if (next == ' ' || next == '{' || next == '(' || next == '.' ||
                next == ')' || next == ',' || next == ']' || next == '[' ||
                next == ';' || next == '*')
                return true;
        }

        return false;
    }
}
