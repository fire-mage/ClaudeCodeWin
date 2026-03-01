namespace ClaudeCodeWin.Services.Highlighting;

public class KotlinTokenizer : ILanguageTokenizer
{
    internal static readonly HashSet<string> Keywords =
    [
        "abstract", "actual", "annotation", "by", "class", "companion", "const",
        "constructor", "crossinline", "data", "delegate", "enum", "expect",
        "external", "final", "fun", "import", "in", "infix", "init", "inline",
        "inner", "interface", "internal", "lateinit", "noinline", "object", "open",
        "operator", "out", "override", "package", "private", "protected", "public",
        "reified", "sealed", "suspend", "tailrec", "typealias", "val",
        "value", "var", "vararg", "where",
    ];

    internal static readonly HashSet<string> ControlKeywords =
    [
        "if", "else", "for", "while", "do", "when", "break", "continue",
        "return", "throw", "try", "catch", "finally", "is", "as",
    ];

    internal static readonly HashSet<string> TypeKeywords =
    [
        "Int", "Long", "Short", "Byte", "Float", "Double", "Boolean", "Char",
        "String", "Unit", "Nothing", "Any", "Array", "List", "Map", "Set",
        "MutableList", "MutableMap", "MutableSet", "Pair", "Triple",
    ];

    internal static readonly HashSet<string> LiteralKeywords =
    [
        "true", "false", "null", "this", "super", "it",
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

            // Block comment: /* ... */ (nestable)
            if (c == '/' && i + 1 < len && text[i + 1] == '*')
            {
                int start = i;
                i += 2;
                int depth = 1;
                while (i < len && depth > 0)
                {
                    if (text[i] == '/' && i + 1 < len && text[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (text[i] == '*' && i + 1 < len && text[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Comment));
                continue;
            }

            // @Annotation — preprocessor
            if (c == '@' && i + 1 < len && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                int start = i;
                i++; // skip @
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
                continue;
            }

            // Raw/multiline string: """..."""
            if (c == '"' && i + 2 < len && text[i + 1] == '"' && text[i + 2] == '"')
            {
                int start = i;
                i += 3; // skip opening """
                while (i < len)
                {
                    if (text[i] == '$' && i + 1 < len)
                    {
                        // String template ${expr}
                        if (text[i + 1] == '{')
                        {
                            if (i > start)
                                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                            i += 2; // skip ${
                            int braceDepth = 1;
                            while (i < len && braceDepth > 0)
                            {
                                if (text[i] == '{') braceDepth++;
                                else if (text[i] == '}') braceDepth--;
                                if (braceDepth > 0) i++;
                            }
                            if (i < len) i++; // skip closing }
                            start = i;
                            continue;
                        }
                        // String template $identifier
                        if (char.IsLetter(text[i + 1]) || text[i + 1] == '_')
                        {
                            if (i > start)
                                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                            i++; // skip $
                            while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                                i++;
                            start = i;
                            continue;
                        }
                    }
                    if (text[i] == '"' && i + 2 < len && text[i + 1] == '"' && text[i + 2] == '"')
                    {
                        i += 3; // consume closing """
                        break;
                    }
                    i++;
                }
                if (i > start)
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Regular string: "..."
            if (c == '"')
            {
                int start = i;
                i++; // skip opening "
                while (i < len && text[i] != '"' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < len)
                    {
                        i += 2; // skip escaped char
                        continue;
                    }
                    if (text[i] == '$' && i + 1 < len)
                    {
                        // String template ${expr}
                        if (text[i + 1] == '{')
                        {
                            if (i > start)
                                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                            i += 2; // skip ${
                            int braceDepth = 1;
                            while (i < len && braceDepth > 0)
                            {
                                if (text[i] == '{') braceDepth++;
                                else if (text[i] == '}') braceDepth--;
                                if (braceDepth > 0) i++;
                            }
                            if (i < len) i++; // skip closing }
                            start = i;
                            continue;
                        }
                        // String template $identifier
                        if (char.IsLetter(text[i + 1]) || text[i + 1] == '_')
                        {
                            if (i > start)
                                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                            i++; // skip $
                            while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                                i++;
                            start = i;
                            continue;
                        }
                    }
                    i++;
                }
                if (i < len && text[i] == '"') i++; // consume closing "
                if (i > start)
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Character literal: '...'
            if (c == '\'')
            {
                int start = i;
                i++; // skip opening '
                if (i < len && text[i] == '\\' && i + 2 < len)
                    i += 2;
                else if (i < len && text[i] != '\'')
                    i++;
                if (i < len && text[i] == '\'') i++; // consume closing '
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Numbers: hex (0x), binary (0b), underscores, L/f suffixes
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

                // Check for !in and !is (preceded by !)
                // These are handled as regular keywords "in" / "is" — the ! is a separate operator

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

                continue;
            }

            // Backtick-quoted identifier: `keyword`
            if (c == '`')
            {
                i++; // skip opening `
                while (i < len && text[i] != '`' && text[i] != '\n')
                    i++;
                if (i < len && text[i] == '`') i++; // consume closing `
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
            i = ScanIntSuffix(text, i, len);
            return i;
        }

        // Binary: 0b...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'b' || text[i + 1] == 'B'))
        {
            i += 2;
            while (i < len && (text[i] == '0' || text[i] == '1' || text[i] == '_')) i++;
            i = ScanIntSuffix(text, i, len);
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

        // Type suffixes: L, uL, f, F
        if (i < len && (text[i] == 'f' || text[i] == 'F'))
            i++;
        else
            i = ScanIntSuffix(text, i, len);

        return i;
    }

    private static int ScanIntSuffix(string text, int i, int len)
    {
        // Kotlin integer suffixes: L, u, U, uL, UL
        if (i < len && (text[i] == 'u' || text[i] == 'U'))
        {
            i++;
            if (i < len && text[i] == 'L') i++;
        }
        else if (i < len && text[i] == 'L')
        {
            i++;
        }
        return i;
    }

    private static bool IsHexDigit(char c)
        => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || c == '_';

    private static bool IsTypeName(string word, string text, int afterWord)
    {
        if (word.Length < 2 || !char.IsUpper(word[0]))
            return false;

        // Skip ALL_CAPS constants
        if (word.All(c => char.IsUpper(c) || c == '_' || char.IsDigit(c)))
            return false;

        if (TypeKeywords.Contains(word))
            return false;

        if (afterWord < text.Length)
        {
            char next = text[afterWord];
            if (next == ' ' || next == '{' || next == '(' || next == '.' ||
                next == ')' || next == ',' || next == ']' || next == '[' ||
                next == ';' || next == '*' || next == '<' || next == '>' ||
                next == ':' || next == '\n' || next == '\r' || next == '?')
                return true;
        }

        return false;
    }
}
