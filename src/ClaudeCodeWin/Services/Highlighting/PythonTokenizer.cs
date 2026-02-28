namespace ClaudeCodeWin.Services.Highlighting;

public class PythonTokenizer : ILanguageTokenizer
{
    internal static readonly HashSet<string> Keywords =
    [
        "and", "as", "assert", "async", "await", "break", "class", "continue",
        "def", "del", "elif", "else", "except", "finally", "for", "from",
        "global", "if", "import", "in", "is", "lambda", "nonlocal", "not",
        "or", "pass", "raise", "return", "try", "while", "with", "yield",
    ];

    internal static readonly HashSet<string> ControlKeywords =
    [
        "if", "elif", "else", "for", "while", "try", "except", "finally",
        "with", "return", "break", "continue", "pass", "raise", "yield",
    ];

    internal static readonly HashSet<string> TypeKeywords =
    [
        "int", "float", "str", "bool", "list", "dict", "tuple", "set",
        "bytes", "bytearray", "complex", "frozenset", "type", "object",
        "range", "memoryview", "property", "classmethod", "staticmethod",
    ];

    internal static readonly HashSet<string> LiteralKeywords =
    [
        "True", "False", "None",
    ];

    internal static readonly HashSet<string> BuiltinFunctions =
    [
        "print", "len", "range", "isinstance", "issubclass",
        "enumerate", "zip", "map", "filter", "sorted", "reversed",
        "open", "input", "abs", "min", "max", "sum", "any", "all",
        "round", "format", "hasattr", "getattr", "setattr", "delattr",
        "super", "iter", "next", "id", "hash", "repr", "dir", "vars",
        "help", "callable", "chr", "ord", "hex", "oct", "bin",
        "pow", "divmod", "eval", "exec", "compile", "globals", "locals",
        "breakpoint", "ascii",
    ];

    // String prefixes (case-insensitive combinations)
    private static readonly HashSet<string> s_stringPrefixes =
    [
        "r", "u", "b", "f", "rb", "br", "rf", "fr",
        "R", "U", "B", "F", "Rb", "bR", "RB", "BR",
        "Rf", "fR", "RF", "FR", "rB", "Br", "rF", "Fr",
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

            // Line comment: #
            if (c == '#')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '\n') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Comment));
                continue;
            }

            // Decorator: @ at start of line
            if (c == '@' && IsLineStart(text, i))
            {
                int start = i;
                i++; // skip @
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
                continue;
            }

            // String with prefix: r"", b"", f"", rb"", fr"", etc.
            if (IsStringPrefixStart(text, i))
            {
                int start = i;
                i = ScanPrefixedString(text, i);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Strings: ", ', """, '''
            if (c == '"' || c == '\'')
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
                else if (BuiltinFunctions.Contains(word) && IsFollowedByParen(text, i))
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.TypeKeyword));
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

    private static bool IsStringPrefixStart(string text, int i)
    {
        int len = text.Length;
        char c = text[i];

        // Must start with a potential prefix letter
        if (c != 'r' && c != 'R' && c != 'b' && c != 'B' &&
            c != 'f' && c != 'F' && c != 'u' && c != 'U')
            return false;

        // Single prefix letter + quote
        if (i + 1 < len && (text[i + 1] == '"' || text[i + 1] == '\''))
            return true;

        // Two-letter prefix + quote (rb, br, rf, fr)
        if (i + 2 < len)
        {
            char c2 = text[i + 1];
            if ((c2 == 'b' || c2 == 'B' || c2 == 'f' || c2 == 'F' ||
                 c2 == 'r' || c2 == 'R') &&
                (text[i + 2] == '"' || text[i + 2] == '\''))
            {
                string prefix = new string([c, c2]);
                return s_stringPrefixes.Contains(prefix);
            }
        }

        return false;
    }

    private static int ScanPrefixedString(string text, int i)
    {
        // Skip prefix letters
        i++;
        if (i < text.Length && text[i] != '"' && text[i] != '\'')
            i++; // two-letter prefix

        return ScanString(text, i);
    }

    private static int ScanString(string text, int i)
    {
        int len = text.Length;
        char quote = text[i];

        // Triple-quoted string: """ or '''
        if (i + 2 < len && text[i + 1] == quote && text[i + 2] == quote)
        {
            i += 3; // skip opening triple
            while (i < len)
            {
                if (text[i] == '\\' && i + 1 < len)
                {
                    i += 2; // skip escape
                    continue;
                }
                if (text[i] == quote && i + 1 < len && text[i + 1] == quote &&
                    i + 2 < len && text[i + 2] == quote)
                {
                    return i + 3; // closing triple
                }
                i++;
            }
            return i; // unterminated
        }

        // Single-quoted string
        i++; // skip opening quote
        while (i < len && text[i] != quote && text[i] != '\n')
        {
            if (text[i] == '\\' && i + 1 < len)
            {
                i += 2; // skip escape
                continue;
            }
            i++;
        }
        if (i < len && text[i] == quote) i++; // skip closing quote
        return i;
    }

    private static int ScanNumber(string text, int i)
    {
        int len = text.Length;

        // Hex: 0x...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'x' || text[i + 1] == 'X'))
        {
            i += 2;
            while (i < len && (IsHexDigit(text[i]) || text[i] == '_')) i++;
            return i;
        }

        // Octal: 0o...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'o' || text[i + 1] == 'O'))
        {
            i += 2;
            while (i < len && ((text[i] >= '0' && text[i] <= '7') || text[i] == '_')) i++;
            return i;
        }

        // Binary: 0b...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'b' || text[i + 1] == 'B'))
        {
            i += 2;
            while (i < len && (text[i] == '0' || text[i] == '1' || text[i] == '_')) i++;
            return i;
        }

        // Decimal / floating point
        while (i < len && (char.IsDigit(text[i]) || text[i] == '_')) i++;

        // Decimal point (Python allows trailing dot: 1. == 1.0)
        if (i < len && text[i] == '.')
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

        // Complex suffix: j or J
        if (i < len && (text[i] == 'j' || text[i] == 'J'))
            i++;

        return i;
    }

    private static bool IsHexDigit(char c)
        => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsLineStart(string text, int pos)
    {
        for (int j = pos - 1; j >= 0; j--)
        {
            if (text[j] == '\n') return true;
            if (!char.IsWhiteSpace(text[j])) return false;
        }
        return true; // start of text
    }

    private static bool IsFollowedByParen(string text, int pos)
    {
        int len = text.Length;
        while (pos < len && char.IsWhiteSpace(text[pos]) && text[pos] != '\n') pos++;
        return pos < len && text[pos] == '(';
    }

    private static bool IsTypeName(string word, string text, int afterWord)
    {
        if (word.Length < 2 || !char.IsUpper(word[0]))
            return false;

        // Skip ALL_CAPS constants
        if (word.All(c => char.IsUpper(c) || c == '_' || char.IsDigit(c)))
            return false;

        // Check context: followed by (, ., <, :, ,, ), used as type hint
        if (afterWord < text.Length)
        {
            char next = text[afterWord];
            if (next == '(' || next == '.' || next == ',' || next == ')' ||
                next == ':' || next == '[' || next == ']')
                return true;
        }

        return false;
    }
}
