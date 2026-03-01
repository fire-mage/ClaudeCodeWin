using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class JavaSnippets
{
    public static IReadOnlyList<CompletionItem> All { get; } = Build();

    private static List<CompletionItem> Build()
    {
        return
        [
            Snippet("class", "class declaration",
                "public class $0 {\r\n    \r\n}"),
            Snippet("interface", "interface declaration",
                "public interface $0 {\r\n    \r\n}"),
            Snippet("enum", "enum declaration",
                "public enum $0 {\r\n    \r\n}"),
            Snippet("record", "record declaration",
                "public record $0() {\r\n}"),
            Snippet("main", "public static void main",
                "public static void main(String[] args) {\r\n    $0\r\n}"),
            Snippet("for", "for loop",
                "for (int i = 0; i < $0; i++) {\r\n    \r\n}"),
            Snippet("fori", "indexed for loop",
                "for (int i = 0; i < $0; i++) {\r\n    \r\n}"),
            Snippet("foreach", "enhanced for loop",
                "for (var item : $0) {\r\n    \r\n}"),
            Snippet("if", "if statement",
                "if ($0) {\r\n    \r\n}"),
            Snippet("switch", "switch statement",
                "switch ($0) {\r\n    case value:\r\n        break;\r\n    default:\r\n        break;\r\n}"),
            Snippet("try", "try-catch",
                "try {\r\n    $0\r\n} catch (Exception e) {\r\n    \r\n}"),
            Snippet("trycf", "try-catch-finally",
                "try {\r\n    $0\r\n} catch (Exception e) {\r\n    \r\n} finally {\r\n    \r\n}"),
            Snippet("sout", "System.out.println",
                "System.out.println($0);"),
            Snippet("method", "method declaration",
                "public void $0() {\r\n    \r\n}"),
            Snippet("ctor", "constructor",
                "public $0() {\r\n    \r\n}"),
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
