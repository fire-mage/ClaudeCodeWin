using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public static class HtmlSnippets
{
    public static readonly List<CompletionItem> Items =
    [
        Snippet("html", "HTML5 boilerplate", "<!DOCTYPE html>\r\n<html lang=\"en\">\r\n<head>\r\n    <meta charset=\"UTF-8\">\r\n    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\r\n    <title>$0</title>\r\n</head>\r\n<body>\r\n    \r\n</body>\r\n</html>"),
        Snippet("div", "div with class", "<div class=\"$0\">\r\n    \r\n</div>"),
        Snippet("table", "table structure", "<table>\r\n    <thead>\r\n        <tr>\r\n            <th>$0</th>\r\n        </tr>\r\n    </thead>\r\n    <tbody>\r\n        <tr>\r\n            <td></td>\r\n        </tr>\r\n    </tbody>\r\n</table>"),
        Snippet("form", "form with action", "<form action=\"$0\" method=\"post\">\r\n    \r\n</form>"),
        Snippet("ul", "unordered list", "<ul>\r\n    <li>$0</li>\r\n</ul>"),
        Snippet("ol", "ordered list", "<ol>\r\n    <li>$0</li>\r\n</ol>"),
        Snippet("link", "CSS stylesheet link", "<link rel=\"stylesheet\" href=\"$0\">"),
        Snippet("script", "script tag", "<script src=\"$0\"></script>"),
        Snippet("meta", "meta tag", "<meta name=\"$0\" content=\"\">"),
        Snippet("img", "image tag", "<img src=\"$0\" alt=\"\">"),
        Snippet("input", "input field", "<input type=\"$0\" name=\"\" value=\"\">"),
        Snippet("a", "anchor link", "<a href=\"$0\"></a>"),
        Snippet("style", "style block", "<style>\r\n    $0\r\n</style>"),
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
