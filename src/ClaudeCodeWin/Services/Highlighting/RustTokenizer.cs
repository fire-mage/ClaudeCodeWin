namespace ClaudeCodeWin.Services.Highlighting;

public class RustTokenizer : ILanguageTokenizer
{
    internal static readonly HashSet<string> Keywords =
    [
        "as", "async", "await", "const", "crate", "dyn", "enum", "extern",
        "fn", "impl", "in", "let", "mod", "move", "mut", "pub", "ref",
        "self", "Self", "static", "struct", "super", "trait", "type",
        "union", "unsafe", "use", "where", "macro_rules",
    ];

    internal static readonly HashSet<string> ControlKeywords =
    [
        "if", "else", "for", "while", "loop", "match", "break",
        "continue", "return", "yield",
    ];

    internal static readonly HashSet<string> TypeKeywords =
    [
        "i8", "i16", "i32", "i64", "i128", "isize",
        "u8", "u16", "u32", "u64", "u128", "usize",
        "f32", "f64", "bool", "char", "str",
        "String", "Vec", "Option", "Result", "Box",
        "Rc", "Arc", "HashMap", "HashSet",
    ];

    internal static readonly HashSet<string> LiteralKeywords =
    [
        "true", "false",
    ];

    internal static readonly HashSet<string> MacroNames =
    [
        "println", "print", "eprintln", "eprint", "format", "write", "writeln",
        "vec", "todo", "unimplemented", "unreachable", "panic", "assert",
        "assert_eq", "assert_ne", "debug_assert", "debug_assert_eq",
        "debug_assert_ne", "cfg", "env", "include", "include_str",
        "include_bytes", "concat", "stringify", "line", "column", "file",
        "module_path", "matches",
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

            // Line comment: // or /// (doc) or //! (inner doc)
            if (c == '/' && i + 1 < len && text[i + 1] == '/')
            {
                int start = i;
                i += 2;
                while (i < len && text[i] != '\n') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Comment));
                continue;
            }

            // Block comment: /* ... */ (nestable!)
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

            // Attributes: #[...] and #![...]
            if (c == '#' && i + 1 < len && (text[i + 1] == '[' || (text[i + 1] == '!' && i + 2 < len && text[i + 2] == '[')))
            {
                int start = i;
                i++; // skip #
                if (i < len && text[i] == '!') i++; // skip !
                i++; // skip [
                int bracketDepth = 1;
                while (i < len && bracketDepth > 0)
                {
                    if (text[i] == '[') bracketDepth++;
                    else if (text[i] == ']') bracketDepth--;
                    if (bracketDepth > 0) i++;
                }
                if (i < len) i++; // consume closing ]
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
                continue;
            }

            // Raw strings: r"...", r#"..."#, r##"..."##, etc.
            // Byte raw strings: br"...", br#"..."#, etc.
            if ((c == 'r' || (c == 'b' && i + 1 < len && text[i + 1] == 'r')) &&
                IsRawStringStart(text, i))
            {
                int start = i;
                i = ScanRawString(text, i);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Byte string: b"..."
            if (c == 'b' && i + 1 < len && text[i + 1] == '"')
            {
                int start = i;
                i += 2; // skip b"
                while (i < len && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < len) i++;
                    i++;
                }
                if (i < len && text[i] == '"') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Regular string: "..." (Rust strings are multi-line)
            if (c == '"')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < len) i++;
                    i++;
                }
                if (i < len && text[i] == '"') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Char literal or lifetime
            if (c == '\'')
            {
                // Lifetime: 'a, 'static, 'lifetime_name
                if (i + 1 < len && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
                {
                    int start = i;
                    i++; // skip '
                    i++; // skip first char
                    while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                        i++;
                    // If followed by another ', it's a char literal like 'a'
                    if (i < len && text[i] == '\'')
                    {
                        i++; // consume closing '
                        tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                    }
                    else
                    {
                        // It's a lifetime — highlight as TypeKeyword
                        tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.TypeKeyword));
                    }
                    continue;
                }

                // Char literal: '\n', '\\'  etc.
                if (i + 1 < len)
                {
                    int start = i;
                    i++; // skip opening '
                    if (i < len && text[i] == '\\' && i + 1 < len)
                    {
                        char escaped = text[i + 1];
                        i += 2; // skip \ and escape char (n, t, u, x, etc.)
                        if (escaped == 'u' && i < len && text[i] == '{')
                        {
                            // Unicode escape: \u{...}
                            i++; // skip {
                            while (i < len && text[i] != '}') i++;
                            if (i < len) i++; // skip }
                        }
                        else if (escaped == 'x')
                        {
                            // Hex escape: \xNN — up to 2 hex digits
                            int count = 0;
                            while (i < len && count < 2 && IsHexDigitStrict(text[i])) { i++; count++; }
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

                i++;
                continue;
            }

            // Numbers: hex (0x), octal (0o), binary (0b), underscores, type suffixes
            if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(text[i + 1])))
            {
                int start = i;
                i = ScanNumber(text, i);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Number));
                continue;
            }

            // Identifiers, keywords, and macro calls (identifier!)
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;

                string word = text[start..i];

                // Check for macro call: identifier followed by ! (but not !=)
                if (i < len && text[i] == '!' && (i + 1 >= len || text[i + 1] != '='))
                {
                    // It's a macro invocation — include the ! in the token
                    i++; // consume !
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.TypeName));
                    continue;
                }

                if (LiteralKeywords.Contains(word))
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Literal));
                else if (word == "self" || word == "Self")
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Keyword));
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

    private static bool IsRawStringStart(string text, int i)
    {
        int len = text.Length;
        // Skip b prefix
        if (text[i] == 'b') i++;
        if (i >= len || text[i] != 'r') return false;
        i++; // skip r
        // Count hashes
        while (i < len && text[i] == '#') i++;
        // Must have a " after r and optional hashes
        return i < len && text[i] == '"';
    }

    private static int ScanRawString(string text, int i)
    {
        int len = text.Length;
        // Skip b prefix
        if (text[i] == 'b') i++;
        i++; // skip r
        // Count opening hashes
        int hashes = 0;
        while (i < len && text[i] == '#')
        {
            hashes++;
            i++;
        }
        i++; // skip opening "

        // Scan until closing " followed by same number of hashes
        while (i < len)
        {
            if (text[i] == '"')
            {
                int j = i + 1;
                int matched = 0;
                while (j < len && matched < hashes && text[j] == '#')
                {
                    matched++;
                    j++;
                }
                if (matched == hashes)
                {
                    return j;
                }
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
            // Optional type suffix
            i = ScanNumberSuffix(text, i);
            return i;
        }

        // Octal: 0o...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'o' || text[i + 1] == 'O'))
        {
            i += 2;
            while (i < len && ((text[i] >= '0' && text[i] <= '7') || text[i] == '_')) i++;
            i = ScanNumberSuffix(text, i);
            return i;
        }

        // Binary: 0b...
        if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'b' || text[i + 1] == 'B'))
        {
            i += 2;
            while (i < len && (text[i] == '0' || text[i] == '1' || text[i] == '_')) i++;
            i = ScanNumberSuffix(text, i);
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

        // Type suffix: i32, u64, f64, etc.
        i = ScanNumberSuffix(text, i);

        return i;
    }

    private static int ScanNumberSuffix(string text, int i)
    {
        int len = text.Length;
        if (i >= len) return i;

        // Check for type suffixes: i8, i16, i32, i64, i128, isize, u8, u16, u32, u64, u128, usize, f32, f64
        if (i + 1 < len && (text[i] == 'i' || text[i] == 'u' || text[i] == 'f'))
        {
            int start = i;
            i++;
            while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            string suffix = text[start..i];
            if (suffix is "i8" or "i16" or "i32" or "i64" or "i128" or "isize"
                or "u8" or "u16" or "u32" or "u64" or "u128" or "usize"
                or "f32" or "f64")
            {
                return i;
            }
            // Not a valid suffix, revert
            return start;
        }

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

        // Already in TypeKeywords — handled separately
        if (TypeKeywords.Contains(word))
            return false;

        if (afterWord < text.Length)
        {
            char next = text[afterWord];
            if (next == ' ' || next == '{' || next == '(' || next == '.' ||
                next == ')' || next == ',' || next == ']' || next == '[' ||
                next == ';' || next == '*' || next == '<' || next == '>' ||
                next == ':' || next == '\n' || next == '\r')
                return true;
        }

        return false;
    }
}
