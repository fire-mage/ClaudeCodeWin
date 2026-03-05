namespace ClaudeCodeWin.Services.Highlighting;

public class PhpTokenizer : ILanguageTokenizer
{
    internal static readonly HashSet<string> Keywords =
    [
        "abstract", "and", "as", "break", "callable", "case", "catch", "class",
        "clone", "const", "continue", "declare", "default", "do", "echo", "else",
        "elseif", "enddeclare", "endfor", "endforeach", "endif", "endswitch",
        "endwhile", "enum", "extends", "final", "finally", "fn", "for", "foreach",
        "function", "global", "goto", "if", "implements", "include", "include_once",
        "instanceof", "insteadof", "interface", "match", "namespace", "new", "or",
        "print", "private", "protected", "public", "readonly", "require",
        "require_once", "return", "static", "switch", "throw", "trait", "try",
        "use", "var", "while", "xor", "yield",
    ];

    internal static readonly HashSet<string> ControlKeywords =
    [
        "if", "else", "elseif", "for", "foreach", "while", "do", "switch",
        "case", "break", "continue", "return", "throw", "try", "catch",
        "finally", "match", "yield",
    ];

    internal static readonly HashSet<string> TypeKeywords =
    [
        "int", "float", "string", "bool", "array", "object", "null", "void",
        "mixed", "never", "true", "false", "self", "parent", "static",
        "iterable", "callable",
    ];

    internal static readonly HashSet<string> LiteralKeywords =
    [
        "true", "false", "null", "TRUE", "FALSE", "NULL",
    ];

    internal static readonly HashSet<string> BuiltinFunctions =
    [
        "echo", "print", "isset", "unset", "empty", "die", "exit",
        "array_push", "array_pop", "array_shift", "array_unshift",
        "array_merge", "array_map", "array_filter", "array_keys",
        "array_values", "array_slice", "array_splice", "array_search",
        "in_array", "count", "sizeof", "sort", "usort", "ksort",
        "str_replace", "str_contains", "str_starts_with", "str_ends_with",
        "strlen", "substr", "strpos", "strtolower", "strtoupper", "trim",
        "explode", "implode", "sprintf", "printf", "var_dump", "print_r",
        "json_encode", "json_decode", "intval", "floatval", "strval",
        "is_array", "is_string", "is_int", "is_null", "is_numeric",
        "class_exists", "function_exists", "method_exists",
        "preg_match", "preg_replace", "preg_split",
        "file_get_contents", "file_put_contents", "file_exists",
        "date", "time", "strtotime", "number_format",
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

            // PHP open tag: <?php (followed by whitespace/EOF) or <?=
            if (c == '<' && i + 1 < len && text[i + 1] == '?')
            {
                int start = i;
                if (i + 4 < len && text[i + 2] == 'p' && text[i + 3] == 'h' && text[i + 4] == 'p'
                    && (i + 5 >= len || !char.IsLetterOrDigit(text[i + 5])))
                {
                    i += 5;
                }
                else if (i + 2 < len && text[i + 2] == '=')
                {
                    i += 3;
                }
                else
                {
                    i++;
                    continue; // bare <? without php or = — not a valid PHP tag, skip
                }
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
                continue;
            }

            // PHP close tag: ?>
            if (c == '?' && i + 1 < len && text[i + 1] == '>')
            {
                tokens.Add(new SyntaxToken(i, 2, SyntaxTokenType.Preprocessor));
                i += 2;
                continue;
            }

            // PHP 8 attribute: #[...]
            if (c == '#' && i + 1 < len && text[i + 1] == '[')
            {
                int start = i;
                i += 2; // skip #[
                int depth = 1;
                while (i < len && depth > 0)
                {
                    if (text[i] == '[') depth++;
                    else if (text[i] == ']') depth--;
                    if (depth > 0) i++;
                }
                if (i < len) i++; // skip closing ]
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
                continue;
            }

            // Line comment: // or #
            if ((c == '/' && i + 1 < len && text[i + 1] == '/') || c == '#')
            {
                int start = i;
                if (c == '/') i += 2; else i++;
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

            // Heredoc/Nowdoc: <<<EOT or <<<'EOT'
            if (c == '<' && i + 2 < len && text[i + 1] == '<' && text[i + 2] == '<')
            {
                int start = i;
                i += 3;
                // Skip whitespace after <<<
                while (i < len && text[i] == ' ') i++;

                bool isNowdoc = i < len && text[i] == '\'';
                if (isNowdoc) i++; // skip opening quote

                // Read identifier
                int idStart = i;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                string heredocId = text[idStart..i];

                if (isNowdoc && i < len && text[i] == '\'') i++; // skip closing quote

                // Skip to end of line
                while (i < len && text[i] != '\n') i++;
                if (i < len) i++; // skip newline

                // Scan for closing identifier at start of line
                if (heredocId.Length > 0)
                {
                    bool found = false;
                    while (i < len && !found)
                    {
                        // Check if line starts with optional whitespace + identifier + optional ;
                        while (i < len && (text[i] == ' ' || text[i] == '\t')) i++;

                        if (i + heredocId.Length <= len &&
                            text.AsSpan(i, heredocId.Length).SequenceEqual(heredocId.AsSpan()))
                        {
                            int afterId = i + heredocId.Length;
                            if (afterId < len && text[afterId] == ';') afterId++;
                            // Valid close only if at end of line or end of file
                            if (afterId >= len || text[afterId] == '\n' || text[afterId] == '\r')
                            {
                                i = afterId;
                                found = true;
                                continue;
                            }
                        }

                        // Skip to next line
                        while (i < len && text[i] != '\n') i++;
                        if (i < len) i++;
                    }
                }

                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Single-quoted string
            // FIX: cap unterminated strings at newline to prevent highlighting rest of file during live editing
            if (c == '\'')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '\'' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < len && text[i + 1] != '\n') i++;
                    i++;
                }
                if (i < len && text[i] == '\'') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // Double-quoted string (with variable interpolation — whole string as String token)
            // FIX: cap unterminated strings at newline to prevent highlighting rest of file during live editing
            if (c == '"')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '"' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < len && text[i + 1] != '\n') i++;
                    i++;
                }
                if (i < len && text[i] == '"') i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.String));
                continue;
            }

            // $variable
            if (c == '$' && i + 1 < len && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                int start = i;
                i++; // skip $
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.TypeName));
                continue;
            }

            // Numbers: hex (0x), octal (0o/0), binary (0b), decimal with underscores
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
        => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsFollowedByParen(string text, int pos)
    {
        int len = text.Length;
        while (pos < len && char.IsWhiteSpace(text[pos]) && text[pos] != '\n') pos++;
        return pos < len && text[pos] == '(';
    }

    private static bool IsTypeName(string word, string text, int afterWord)
    {
        // PascalCase heuristic: starts with uppercase, at least 2 chars, has a lowercase letter
        if (word.Length < 2 || !char.IsUpper(word[0]))
            return false;

        // Skip ALL_CAPS constants
        if (word.All(c => char.IsUpper(c) || c == '_' || char.IsDigit(c)))
            return false;

        // Check context: followed by space, <, (, ., {, [, ), ], ,, ;, ::
        if (afterWord < text.Length)
        {
            char next = text[afterWord];
            if (next == ' ' || next == '<' || next == '(' || next == '.' ||
                next == '>' || next == ',' || next == ')' || next == ']' ||
                next == '{' || next == ';' || next == '[' || next == ':')
                return true;
        }

        return false;
    }
}
