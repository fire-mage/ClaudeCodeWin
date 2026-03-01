using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class JavaScriptSnippets
{
    public static IReadOnlyList<CompletionItem> All { get; } = Build();

    private static List<CompletionItem> Build()
    {
        return
        [
            Snippet("function", "function declaration",
                "function $0() {\r\n    \r\n}"),
            Snippet("arrow", "arrow function",
                "const $0 = () => {\r\n    \r\n};"),
            Snippet("class", "class declaration",
                "class $0 {\r\n    constructor() {\r\n        \r\n    }\r\n}"),
            Snippet("if", "if statement",
                "if ($0) {\r\n    \r\n}"),
            Snippet("for", "for loop",
                "for (let i = 0; i < $0; i++) {\r\n    \r\n}"),
            Snippet("forof", "for...of loop",
                "for (const $0 of iterable) {\r\n    \r\n}"),
            Snippet("forin", "for...in loop",
                "for (const $0 in object) {\r\n    \r\n}"),
            Snippet("while", "while loop",
                "while ($0) {\r\n    \r\n}"),
            Snippet("switch", "switch statement",
                "switch ($0) {\r\n    case value:\r\n        break;\r\n    default:\r\n        break;\r\n}"),
            Snippet("try", "try-catch",
                "try {\r\n    $0\r\n} catch (error) {\r\n    \r\n}"),
            Snippet("trycf", "try-catch-finally",
                "try {\r\n    $0\r\n} catch (error) {\r\n    \r\n} finally {\r\n    \r\n}"),
            Snippet("import", "import statement",
                "import { $0 } from 'module';"),
            Snippet("export", "export default",
                "export default $0;"),
            Snippet("async", "async function",
                "async function $0() {\r\n    \r\n}"),
            Snippet("promise", "new Promise",
                "new Promise((resolve, reject) => {\r\n    $0\r\n});"),
            Snippet("map", "array map",
                ".map(($0) => {\r\n    \r\n})"),
            Snippet("filter", "array filter",
                ".filter(($0) => {\r\n    \r\n})"),
            Snippet("console", "console.log",
                "console.log($0);"),
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
