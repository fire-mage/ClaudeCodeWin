using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class SqlSnippets
{
    public static IReadOnlyList<CompletionItem> All { get; } = Build();

    private static List<CompletionItem> Build()
    {
        return
        [
            Snippet("select", "SELECT ... FROM",
                "SELECT $0\r\nFROM "),
            Snippet("insert", "INSERT INTO ... VALUES",
                "INSERT INTO $0 ()\r\nVALUES ()"),
            Snippet("update", "UPDATE ... SET ... WHERE",
                "UPDATE $0\r\nSET \r\nWHERE "),
            Snippet("delete", "DELETE FROM ... WHERE",
                "DELETE FROM $0\r\nWHERE "),
            Snippet("create", "CREATE TABLE",
                "CREATE TABLE $0 (\r\n    id INT PRIMARY KEY,\r\n    \r\n)"),
            Snippet("alter", "ALTER TABLE",
                "ALTER TABLE $0\r\nADD "),
            Snippet("join", "SELECT ... JOIN ... ON",
                "SELECT $0\r\nFROM \r\nJOIN  ON  = "),
            Snippet("cte", "WITH ... AS (common table expression)",
                "WITH $0 AS (\r\n    SELECT \r\n    FROM \r\n)\r\nSELECT * FROM "),
            Snippet("case", "CASE WHEN ... THEN ... END",
                "CASE\r\n    WHEN $0 THEN \r\n    ELSE \r\nEND"),
            Snippet("index", "CREATE INDEX",
                "CREATE INDEX $0 ON  ()"),
            Snippet("proc", "CREATE PROCEDURE",
                "CREATE PROCEDURE $0\r\nAS\r\nBEGIN\r\n    \r\nEND"),
            Snippet("view", "CREATE VIEW",
                "CREATE VIEW $0 AS\r\nSELECT \r\nFROM "),
            Snippet("ifthen", "IF ... BEGIN ... END",
                "IF $0\r\nBEGIN\r\n    \r\nEND"),
        ];
    }

    private static CompletionItem Snippet(string label, string detail, string template)
    {
        int markerIndex = template.IndexOf("$0", StringComparison.Ordinal);
        string insertText = markerIndex >= 0 ? template.Remove(markerIndex, 2) : template;
        int caretOffset = markerIndex >= 0 ? markerIndex : -1;

        return new CompletionItem
        {
            Label = label,
            InsertText = insertText,
            Kind = CompletionItemKind.Snippet,
            Detail = detail,
            CaretOffset = caretOffset,
            SortPriority = 0,
        };
    }
}
