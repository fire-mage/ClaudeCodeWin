using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class KotlinSnippets
{
    public static IReadOnlyList<CompletionItem> All { get; } = Build();

    private static List<CompletionItem> Build()
    {
        return
        [
            Snippet("fun", "function declaration",
                "fun $0() {\r\n    \r\n}"),
            Snippet("class", "class declaration",
                "class $0 {\r\n    \r\n}"),
            Snippet("data", "data class declaration",
                "data class $0(\r\n    \r\n)"),
            Snippet("sealed", "sealed class declaration",
                "sealed class $0 {\r\n    \r\n}"),
            Snippet("object", "object declaration",
                "object $0 {\r\n    \r\n}"),
            Snippet("interface", "interface declaration",
                "interface $0 {\r\n    \r\n}"),
            Snippet("enum", "enum class declaration",
                "enum class $0 {\r\n    \r\n}"),
            Snippet("if", "if expression",
                "if ($0) {\r\n    \r\n}"),
            Snippet("for", "for loop",
                "for ($0 in ) {\r\n    \r\n}"),
            Snippet("when", "when expression",
                "when ($0) {\r\n    \r\n}"),
            Snippet("try", "try-catch block",
                "try {\r\n    $0\r\n} catch (e: Exception) {\r\n    \r\n}"),
            Snippet("trycf", "try-catch-finally block",
                "try {\r\n    $0\r\n} catch (e: Exception) {\r\n    \r\n} finally {\r\n    \r\n}"),
            Snippet("main", "main function",
                "fun main(args: Array<String>) {\r\n    $0\r\n}"),
            Snippet("lambda", "lambda expression",
                "{ $0 ->\r\n    \r\n}"),
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
