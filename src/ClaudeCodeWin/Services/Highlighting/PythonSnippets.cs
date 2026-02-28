using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class PythonSnippets
{
    public static IReadOnlyList<CompletionItem> All { get; } = Build();

    private static List<CompletionItem> Build()
    {
        return
        [
            Snippet("def", "function definition",
                "def $0():\r\n    pass"),
            Snippet("class", "class definition",
                "class $0:\r\n    def __init__(self):\r\n        pass"),
            Snippet("if", "if statement",
                "if $0:\r\n    pass"),
            Snippet("elif", "elif clause",
                "elif $0:\r\n    pass"),
            Snippet("else", "else clause",
                "else:\r\n    $0"),
            Snippet("for", "for loop",
                "for $0 in :\r\n    pass"),
            Snippet("while", "while loop",
                "while $0:\r\n    pass"),
            Snippet("try", "try/except",
                "try:\r\n    $0\r\nexcept Exception as e:\r\n    pass"),
            Snippet("tryf", "try/except/finally",
                "try:\r\n    $0\r\nexcept Exception as e:\r\n    pass\r\nfinally:\r\n    pass"),
            Snippet("with", "context manager",
                "with $0 as :\r\n    pass"),
            Snippet("lambda", "lambda expression",
                "lambda $0: "),
            Snippet("prop", "property decorator",
                "@property\r\ndef $0(self):\r\n    return self._"),
            Snippet("main", "if __name__ == '__main__'",
                "if __name__ == '__main__':\r\n    $0"),
            Snippet("listcomp", "list comprehension",
                "[$0 for x in ]"),
            Snippet("dictcomp", "dict comprehension",
                "{$0: v for k, v in }"),
            Snippet("async", "async function",
                "async def $0():\r\n    pass"),
            Snippet("import", "import statement",
                "from $0 import "),
            Snippet("dataclass", "dataclass",
                "@dataclass\r\nclass $0:\r\n    "),
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
