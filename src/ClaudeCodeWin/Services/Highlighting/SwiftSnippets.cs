using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class SwiftSnippets
{
    public static IReadOnlyList<CompletionItem> All { get; } = Build();

    private static List<CompletionItem> Build()
    {
        return
        [
            Snippet("class", "class declaration",
                "class $0 {\r\n    \r\n}"),
            Snippet("struct", "struct declaration",
                "struct $0 {\r\n    \r\n}"),
            Snippet("enum", "enum declaration",
                "enum $0 {\r\n    \r\n}"),
            Snippet("protocol", "protocol declaration",
                "protocol $0 {\r\n    \r\n}"),
            Snippet("func", "function declaration",
                "func $0() {\r\n    \r\n}"),
            Snippet("init", "initializer",
                "init($0) {\r\n    \r\n}"),
            Snippet("guard", "guard let ... else",
                "guard let $0 else {\r\n    return\r\n}"),
            Snippet("if", "if statement",
                "if $0 {\r\n    \r\n}"),
            Snippet("iflet", "if let unwrap",
                "if let $0 {\r\n    \r\n}"),
            Snippet("for", "for-in loop",
                "for $0 in  {\r\n    \r\n}"),
            Snippet("switch", "switch statement",
                "switch $0 {\r\ncase :\r\n    break\r\ndefault:\r\n    break\r\n}"),
            Snippet("do", "do-catch block",
                "do {\r\n    $0\r\n} catch {\r\n    \r\n}"),
            Snippet("extension", "extension declaration",
                "extension $0 {\r\n    \r\n}"),
            Snippet("closure", "closure expression",
                "{ $0 in\r\n    \r\n}"),
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
