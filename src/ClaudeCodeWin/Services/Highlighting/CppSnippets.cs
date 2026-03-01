using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class CppSnippets
{
    public static IReadOnlyList<CompletionItem> All { get; } = Build();

    private static List<CompletionItem> Build()
    {
        return
        [
            Snippet("class", "class declaration",
                "class $0 {\r\npublic:\r\n    \r\nprivate:\r\n    \r\n};"),
            Snippet("struct", "struct declaration",
                "struct $0 {\r\n    \r\n};"),
            Snippet("enum", "enum declaration",
                "enum $0 {\r\n    \r\n};"),
            Snippet("for", "for loop",
                "for (int i = 0; i < $0; i++) {\r\n    \r\n}"),
            Snippet("fori", "range-based for loop",
                "for (const auto& $0 : ) {\r\n    \r\n}"),
            Snippet("while", "while loop",
                "while ($0) {\r\n    \r\n}"),
            Snippet("if", "if statement",
                "if ($0) {\r\n    \r\n}"),
            Snippet("switch", "switch statement",
                "switch ($0) {\r\n    case :\r\n        break;\r\n    default:\r\n        break;\r\n}"),
            Snippet("try", "try-catch",
                "try {\r\n    $0\r\n} catch (const std::exception& e) {\r\n    \r\n}"),
            Snippet("main", "main function",
                "int main(int argc, char* argv[]) {\r\n    $0\r\n    return 0;\r\n}"),
            Snippet("include", "#include directive",
                "#include <$0>"),
            Snippet("ifndef", "header guard",
                "#ifndef GUARD_H\r\n#define GUARD_H\r\n\r\n$0\r\n\r\n#endif // GUARD_H"),
            Snippet("template", "template declaration",
                "template <typename $0>\r\n"),
            Snippet("lambda", "lambda expression",
                "[$0](auto& ) {\r\n    \r\n}"),
            Snippet("vector", "std::vector declaration",
                "std::vector<$0> "),
            Snippet("cout", "std::cout output",
                "std::cout << $0 << std::endl;"),
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
