using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class GoSnippets
{
    public static IReadOnlyList<CompletionItem> All { get; } = Build();

    private static List<CompletionItem> Build()
    {
        return
        [
            Snippet("func", "function declaration",
                "func $0() {\r\n    \r\n}"),
            Snippet("main", "func main",
                "func main() {\r\n    $0\r\n}"),
            Snippet("if", "if statement",
                "if $0 {\r\n    \r\n}"),
            Snippet("iferr", "if err != nil",
                "if err != nil {\r\n    $0\r\n}"),
            Snippet("for", "for loop",
                "for $0 {\r\n    \r\n}"),
            Snippet("forr", "for range",
                "for _, v := range $0 {\r\n    \r\n}"),
            Snippet("switch", "switch statement",
                "switch $0 {\r\ncase :\r\n    \r\ndefault:\r\n    \r\n}"),
            Snippet("struct", "struct declaration",
                "type $0 struct {\r\n    \r\n}"),
            Snippet("interface", "interface declaration",
                "type $0 interface {\r\n    \r\n}"),
            Snippet("method", "method with receiver",
                "func (r *$0) Method() {\r\n    \r\n}"),
            Snippet("goroutine", "go func()",
                "go func() {\r\n    $0\r\n}()"),
            Snippet("select", "select statement",
                "select {\r\ncase $0:\r\n    \r\ndefault:\r\n    \r\n}"),
            Snippet("defer", "defer statement",
                "defer $0"),
            Snippet("test", "test function",
                "func Test$0(t *testing.T) {\r\n    \r\n}"),
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
