using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class PhpSnippets
{
    public static IReadOnlyList<CompletionItem> All { get; } = Build();

    private static List<CompletionItem> Build()
    {
        return
        [
            Snippet("class", "class declaration",
                "class $0 {\r\n    \r\n}"),
            Snippet("interface", "interface declaration",
                "interface $0 {\r\n    \r\n}"),
            Snippet("trait", "trait declaration",
                "trait $0 {\r\n    \r\n}"),
            Snippet("function", "function declaration",
                "function $0() {\r\n    \r\n}"),
            Snippet("foreach", "foreach loop",
                "foreach ($0 as $item) {\r\n    \r\n}"),
            Snippet("for", "for loop",
                "for ($i = 0; $i < $0; $i++) {\r\n    \r\n}"),
            Snippet("if", "if statement",
                "if ($0) {\r\n    \r\n}"),
            Snippet("switch", "switch statement",
                "switch ($0) {\r\n    case value:\r\n        break;\r\n    default:\r\n        break;\r\n}"),
            Snippet("try", "try-catch",
                "try {\r\n    $0\r\n} catch (\\Exception $e) {\r\n    \r\n}"),
            Snippet("trycf", "try-catch-finally",
                "try {\r\n    $0\r\n} catch (\\Exception $e) {\r\n    \r\n} finally {\r\n    \r\n}"),
            Snippet("match", "match expression",
                "match ($0) {\r\n    value => result,\r\n    default => result,\r\n}"),
            Snippet("enum", "enum declaration",
                "enum $0 {\r\n    \r\n}"),
            Snippet("constructor", "__construct method",
                "public function __construct($0) {\r\n    \r\n}"),
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
