using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class CssSnippets
{
    public static readonly List<CompletionItem> Items =
    [
        Snippet("media", "@media query", "@media ($0) {\r\n    \r\n}"),
        Snippet("flex", "flexbox container", "display: flex;\r\nalign-items: $0;\r\njustify-content: center;"),
        Snippet("grid", "grid container", "display: grid;\r\ngrid-template-columns: $0;\r\ngap: 1rem;"),
        Snippet("anim", "@keyframes animation", "@keyframes $0 {\r\n    from {\r\n        \r\n    }\r\n    to {\r\n        \r\n    }\r\n}"),
        Snippet("trans", "transition", "transition: $0 0.3s ease;"),
        Snippet("bg", "background shorthand", "background: $0 no-repeat center / cover;"),
        Snippet("font", "font shorthand", "font: $0 16px/1.5 sans-serif;"),
        Snippet("border", "border shorthand", "border: 1px solid $0;"),
        Snippet("center", "center with flexbox", "display: flex;\r\nalign-items: center;\r\njustify-content: center;$0"),
        Snippet("abs", "absolute positioning", "position: absolute;\r\ntop: $0;\r\nleft: 0;\r\nright: 0;\r\nbottom: 0;"),
    ];

    private static CompletionItem Snippet(string label, string detail, string template)
    {
        // BUG FIX: use Ordinal comparison to avoid culture-sensitive string matching
        int caretOffset = template.IndexOf("$0", StringComparison.Ordinal);
        string insertText = caretOffset >= 0 ? template.Remove(caretOffset, 2) : template;
        return new CompletionItem
        {
            Label = label,
            InsertText = insertText,
            Kind = CompletionItemKind.Snippet,
            Detail = detail,
            CaretOffset = caretOffset >= 0 ? caretOffset : -1,
            SortPriority = 0,
        };
    }
}
