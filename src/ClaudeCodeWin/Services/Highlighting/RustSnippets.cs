using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class RustSnippets
{
    public static IReadOnlyList<CompletionItem> All { get; } = Build();

    private static List<CompletionItem> Build()
    {
        return
        [
            Snippet("fn", "function declaration",
                "fn $0() {\r\n    \r\n}"),
            Snippet("main", "fn main",
                "fn main() {\r\n    $0\r\n}"),
            Snippet("struct", "struct declaration",
                "struct $0 {\r\n    \r\n}"),
            Snippet("enum", "enum declaration",
                "enum $0 {\r\n    \r\n}"),
            Snippet("impl", "impl block",
                "impl $0 {\r\n    \r\n}"),
            Snippet("trait", "trait declaration",
                "trait $0 {\r\n    \r\n}"),
            Snippet("match", "match expression",
                "match $0 {\r\n    _ => {},\r\n}"),
            Snippet("if", "if expression",
                "if $0 {\r\n    \r\n}"),
            Snippet("for", "for loop",
                "for $0 in  {\r\n    \r\n}"),
            Snippet("while", "while loop",
                "while $0 {\r\n    \r\n}"),
            Snippet("loop", "infinite loop",
                "loop {\r\n    $0\r\n}"),
            Snippet("let", "let binding",
                "let $0 = "),
            Snippet("mod", "module declaration",
                "mod $0 {\r\n    \r\n}"),
            Snippet("test", "test function",
                "#[test]\r\nfn $0() {\r\n    \r\n}"),
            Snippet("derive", "derive attribute",
                "#[derive($0)]"),
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
