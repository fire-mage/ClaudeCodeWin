using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class CSharpSnippets
{
    public static IReadOnlyList<CompletionItem> All { get; } = Build();

    private static List<CompletionItem> Build()
    {
        return
        [
            Snippet("for", "for loop",
                "for (int i = 0; i < $0; i++)\r\n{\r\n    \r\n}"),
            Snippet("foreach", "foreach loop",
                "foreach (var $0 in collection)\r\n{\r\n    \r\n}"),
            Snippet("if", "if statement",
                "if ($0)\r\n{\r\n    \r\n}"),
            Snippet("else", "else block",
                "else\r\n{\r\n    $0\r\n}"),
            Snippet("while", "while loop",
                "while ($0)\r\n{\r\n    \r\n}"),
            Snippet("do", "do-while loop",
                "do\r\n{\r\n    $0\r\n} while ();"),
            Snippet("switch", "switch statement",
                "switch ($0)\r\n{\r\n    case :\r\n        break;\r\n}"),
            Snippet("try", "try-catch",
                "try\r\n{\r\n    $0\r\n}\r\ncatch (Exception ex)\r\n{\r\n    \r\n}"),
            Snippet("trycf", "try-catch-finally",
                "try\r\n{\r\n    $0\r\n}\r\ncatch (Exception ex)\r\n{\r\n    \r\n}\r\nfinally\r\n{\r\n    \r\n}"),
            Snippet("prop", "auto property",
                "public $0 Name { get; set; }"),
            Snippet("propfull", "full property",
                "private $0 _name;\r\npublic Type Name\r\n{\r\n    get => _name;\r\n    set => _name = value;\r\n}"),
            Snippet("ctor", "constructor",
                "public $0()\r\n{\r\n    \r\n}"),
            Snippet("class", "class declaration",
                "public class $0\r\n{\r\n    \r\n}"),
            Snippet("interface", "interface declaration",
                "public interface $0\r\n{\r\n    \r\n}"),
            Snippet("enum", "enum declaration",
                "public enum $0\r\n{\r\n    \r\n}"),
            Snippet("cw", "Console.WriteLine",
                "Console.WriteLine($0);"),
            Snippet("region", "#region",
                "#region $0\r\n\r\n#endregion"),
        ];
    }

    private static CompletionItem Snippet(string label, string detail, string template)
    {
        // Find $0 marker position for caret placement
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
