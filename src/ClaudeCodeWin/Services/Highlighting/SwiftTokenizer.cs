namespace ClaudeCodeWin.Services.Highlighting;

public class SwiftTokenizer : ILanguageTokenizer
{
    internal static readonly HashSet<string> Keywords =
    [
        "actor", "associatedtype", "class", "deinit", "enum", "extension",
        "fileprivate", "func", "import", "init", "inout", "internal", "let",
        "nonisolated", "open", "operator", "private", "protocol", "public",
        "rethrows", "some", "static", "struct", "subscript", "typealias",
        "var", "any", "macro", "borrowing", "consuming", "package",
    ];

    internal static readonly HashSet<string> ControlKeywords =
    [
        "if", "else", "for", "while", "repeat", "switch", "case", "default",
        "break", "continue", "return", "throw", "throws", "try", "catch",
        "do", "defer", "guard", "where", "fallthrough", "in", "as", "is",
    ];

    internal static readonly HashSet<string> TypeKeywords =
    [
        "Int", "Int8", "Int16", "Int32", "Int64", "UInt", "UInt8",
        "Float", "Double", "Bool", "String", "Character",
        "Array", "Dictionary", "Set", "Optional", "Result",
        "Void", "Any", "AnyObject", "Never", "Error",
        "Codable", "Hashable", "Equatable", "Comparable",
    ];

    internal static readonly HashSet<string> LiteralKeywords =
    [
        "true", "false", "nil", "self", "Self", "super",
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

            // @attribute — preprocessor
            if (c == '@' && i + 1 < len && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                int start = i;
                i++; // skip @
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
                continue;
            }

            // #directive — preprocessor (#if, #else, #endif, #selector, #available, #warning, #error)
            if (c == '#' && i + 1 < len && char.IsLetter(text[i + 1]))
            {
                int start = i;
                i++; // skip #
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
                continue;
            }

            // Multiline string: """..."""
            if (c == '"' && i + 2 < len && text[i + 1] == '"' && text[i + 2] == '"')
            {
                int start = i;
                i += 3; // skip opening """
                while (i < len)
                {
                    if (text[i] == '\\' && i + 1 < len)
                    {
                        // Handle \(expr) interpolation — skip balanced parens
                        if (text[i + 1] == '(')
                        {
                            // Emit string token up to backslash
                            if (i > start)
                                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));

                            int interpStart = i;
                            i += 2; // skip \(
                            int parenDepth = 1;
                            while (i < len && parenDepth > 0)
                            {
                                if (text[i] == '(') parenDepth++;
                                else if (text[i] == ')') parenDepth--;
                                if (parenDepth > 0) i++;
                            }
                            if (i < len) i++; // skip closing )
                            // Interpolation delimiters — skip (not highlighted as string)
                            start = i;
                            continue;
                        }
                        i += 2; // skip escaped char
                        continue;
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
                        // Handle \(expr) interpolation
                        if (text[i + 1] == '(')
                        {
                            // Emit string token up to backslash
                            if (i > start)
                                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));

                            i += 2; // skip \(
                            int parenDepth = 1;
                            while (i < len && parenDepth > 0)
                            {
                                if (text[i] == '(') parenDepth++;
                                else if (text[i] == ')') parenDepth--;
                                if (parenDepth > 0) i++;
                            }
                            if (i < len) i++; // skip closing )
                            start = i;
                            continue;
                        }
                        i += 2; // skip escaped char
                        continue;
                    }
                    i++;
                }
                if (i < len && text[i] == '"') i++; // consume closing "
                if (i > start)
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Numbers: hex (0x), octal (0o), binary (0b), underscores
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
