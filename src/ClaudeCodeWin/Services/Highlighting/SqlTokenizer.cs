namespace ClaudeCodeWin.Services.Highlighting;

public class SqlTokenizer : ILanguageTokenizer
{
    internal static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "INSERT", "INTO", "VALUES", "UPDATE", "SET",
        "DELETE", "CREATE", "ALTER", "DROP", "TABLE", "INDEX", "VIEW", "DATABASE",
        "SCHEMA", "GRANT", "REVOKE", "TRIGGER", "PROCEDURE", "FUNCTION", "BEGIN",
        "END", "DECLARE", "EXEC", "EXECUTE", "CALL", "WITH", "AS", "ON", "JOIN",
        "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "NATURAL", "UNION",
        "ALL", "INTERSECT", "EXCEPT", "HAVING", "GROUP", "BY", "ORDER", "ASC",
        "DESC", "LIMIT", "OFFSET", "FETCH", "NEXT", "TOP", "DISTINCT", "EXISTS",
        "BETWEEN", "LIKE", "IN", "ANY", "SOME", "CASE", "WHEN", "THEN", "ELSE",
        "AND", "OR", "NOT", "IS", "CONSTRAINT", "PRIMARY", "KEY", "FOREIGN",
        "REFERENCES", "UNIQUE", "CHECK", "DEFAULT", "AUTO_INCREMENT", "IDENTITY",
        "CASCADE", "TRUNCATE", "COMMIT", "ROLLBACK", "SAVEPOINT", "TRANSACTION",
        "IF", "WHILE", "RETURN", "GOTO", "BREAK", "CONTINUE", "TRY", "CATCH",
        "THROW", "RAISE",
    };

    internal static readonly HashSet<string> ControlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "IF", "ELSE", "WHILE", "CASE", "WHEN", "THEN", "BEGIN", "END",
        "RETURN", "BREAK", "CONTINUE", "TRY", "CATCH",
    };

    internal static readonly HashSet<string> TypeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INT", "INTEGER", "BIGINT", "SMALLINT", "TINYINT", "DECIMAL", "NUMERIC",
        "FLOAT", "REAL", "DOUBLE", "BIT", "CHAR", "VARCHAR", "NCHAR", "NVARCHAR",
        "TEXT", "NTEXT", "DATE", "TIME", "DATETIME", "DATETIME2", "TIMESTAMP",
        "BINARY", "VARBINARY", "IMAGE", "BLOB", "CLOB", "BOOLEAN", "UUID",
        "SERIAL", "JSON", "JSONB", "XML", "MONEY", "UNIQUEIDENTIFIER",
    };

    internal static readonly HashSet<string> LiteralKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NULL", "TRUE", "FALSE", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP",
    };

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

            // Line comment: --
            if (c == '-' && i + 1 < len && text[i + 1] == '-')
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

            // Single-quoted string (SQL standard)
            if (c == '\'')
            {
                int start = i;
                i++; // skip opening '
                while (i < len)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < len && text[i + 1] == '\'')
                        {
                            i += 2; // escaped '' in SQL
                            continue;
                        }
                        i++; // closing '
                        break;
                    }
                    i++;
                }
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

            // @ variables (T-SQL: @variable, @@global)
            if (c == '@')
            {
                int start = i;
                i++; // skip first @
                if (i < len && text[i] == '@') i++; // @@global
                if (i < len && (char.IsLetter(text[i]) || text[i] == '_'))
                {
                    i++;
                    while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                        i++;
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
                }
                continue;
            }

            // : bind parameters (Oracle/PostgreSQL)
            if (c == ':' && i + 1 < len && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                int start = i;
                i++; // skip :
                i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenType.Preprocessor));
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

        // Decimal / floating point
        while (i < len && char.IsDigit(text[i])) i++;

        // Decimal point
        if (i < len && text[i] == '.' && i + 1 < len && char.IsDigit(text[i + 1]))
        {
            i++;
            while (i < len && char.IsDigit(text[i])) i++;
        }

        // Exponent (scientific notation)
        if (i < len && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;
            if (i < len && (text[i] == '+' || text[i] == '-')) i++;
            while (i < len && char.IsDigit(text[i])) i++;
        }

        return i;
    }
}
