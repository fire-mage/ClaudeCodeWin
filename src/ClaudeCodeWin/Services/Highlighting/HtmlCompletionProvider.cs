using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public class HtmlCompletionProvider : ICompletionProvider
{
    private static readonly string[] Tags =
    [
        // Document
        "html", "head", "body", "title",
        // Metadata
        "meta", "link", "base", "style", "script", "noscript",
        // Sections
        "header", "footer", "nav", "main", "article", "section", "aside", "address",
        // Headings
        "h1", "h2", "h3", "h4", "h5", "h6", "hgroup",
        // Block content
        "div", "p", "blockquote", "pre", "figure", "figcaption", "hr", "details", "summary", "dialog",
        // Lists
        "ul", "ol", "li", "dl", "dt", "dd", "menu",
        // Inline
        "span", "a", "strong", "em", "b", "i", "u", "s", "small", "mark", "abbr",
        "code", "kbd", "samp", "var", "sub", "sup", "br", "wbr", "time", "data", "cite", "q", "dfn",
        // Table
        "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
        // Forms
        "form", "fieldset", "legend", "label", "input", "button", "select", "option", "optgroup",
        "textarea", "output", "datalist", "progress", "meter",
        // Media
        "img", "picture", "source", "video", "audio", "track", "canvas", "svg", "iframe",
        "embed", "object", "param", "map", "area",
        // Template
        "template", "slot",
    ];

    // Global HTML attributes
    private static readonly string[] GlobalAttributes =
    [
        "class", "id", "style", "title", "tabindex", "lang", "dir", "hidden", "draggable",
        "contenteditable", "spellcheck", "translate", "autofocus", "role",
        "data-", "aria-",
        // Event handlers
        "onclick", "onchange", "onsubmit", "onload", "onerror", "onfocus", "onblur",
        "onmouseover", "onmouseout", "onkeydown", "onkeyup", "oninput",
    ];

    // Tag-specific attributes
    private static readonly Dictionary<string, string[]> TagAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = ["href", "target", "rel", "download", "type", "hreflang"],
        ["img"] = ["src", "alt", "width", "height", "loading", "decoding", "srcset", "sizes"],
        ["input"] = ["type", "name", "value", "placeholder", "required", "disabled", "readonly",
                      "checked", "min", "max", "step", "pattern", "maxlength", "minlength", "autocomplete", "autofocus", "list", "multiple", "accept"],
        ["button"] = ["type", "disabled", "name", "value", "form"],
        ["form"] = ["action", "method", "enctype", "target", "autocomplete", "novalidate"],
        ["select"] = ["name", "multiple", "size", "required", "disabled"],
        ["option"] = ["value", "selected", "disabled", "label"],
        ["textarea"] = ["name", "rows", "cols", "placeholder", "required", "disabled", "readonly", "maxlength", "wrap"],
        ["label"] = ["for"],
        ["link"] = ["rel", "href", "type", "media", "sizes", "crossorigin", "integrity"],
        ["script"] = ["src", "type", "async", "defer", "crossorigin", "integrity", "nomodule"],
        ["style"] = ["type", "media"],
        ["meta"] = ["name", "content", "charset", "http-equiv", "property"],
        ["iframe"] = ["src", "width", "height", "sandbox", "allow", "loading", "name", "srcdoc"],
        ["video"] = ["src", "controls", "autoplay", "loop", "muted", "poster", "width", "height", "preload"],
        ["audio"] = ["src", "controls", "autoplay", "loop", "muted", "preload"],
        ["source"] = ["src", "type", "media", "srcset", "sizes"],
        ["track"] = ["src", "kind", "srclang", "label", "default"],
        ["canvas"] = ["width", "height"],
        ["table"] = ["border", "cellpadding", "cellspacing"],
        ["td"] = ["colspan", "rowspan", "headers"],
        ["th"] = ["colspan", "rowspan", "headers", "scope"],
        ["colgroup"] = ["span"],
        ["col"] = ["span"],
        ["ol"] = ["type", "start", "reversed"],
        ["details"] = ["open"],
        ["dialog"] = ["open"],
        ["progress"] = ["value", "max"],
        ["meter"] = ["value", "min", "max", "low", "high", "optimum"],
        ["time"] = ["datetime"],
        ["data"] = ["value"],
        ["object"] = ["data", "type", "width", "height", "name"],
        ["map"] = ["name"],
        ["area"] = ["shape", "coords", "href", "alt", "target"],
    };

    // Snippet labels to exclude from tag completions
    private static readonly HashSet<string> SnippetLabels = new(HtmlSnippets.Items.Select(s => s.Label));

    private enum HtmlContext { Text, AfterOpenAngle, InsideTag, InsideAttributeValue }

    public List<CompletionItem> GetCompletions(string text, int caretPosition, CompletionTrigger trigger, List<SyntaxToken> tokens)
    {
        var context = DetermineContext(text, caretPosition);

        var items = new List<CompletionItem>();

        switch (context.Context)
        {
            case HtmlContext.AfterOpenAngle:
            case HtmlContext.Text when trigger == CompletionTrigger.Manual:
            {
                // Show tags
                var (_, prefix) = GetWordAtCaret(text, caretPosition);
                foreach (var tag in Tags)
                {
                    if (SnippetLabels.Contains(tag)) continue;
                    items.Add(new CompletionItem
                    {
                        Label = tag,
                        InsertText = tag,
                        Kind = CompletionItemKind.Tag,
                        SortPriority = 1,
                    });
                }
                // Add snippets — strip leading '<' when triggered after '<' to avoid duplication
                foreach (var snippet in HtmlSnippets.Items)
                {
                    if (context.Context == HtmlContext.AfterOpenAngle && snippet.InsertText.StartsWith('<'))
                    {
                        items.Add(new CompletionItem
                        {
                            Label = snippet.Label,
                            InsertText = snippet.InsertText[1..],
                            Kind = snippet.Kind,
                            Detail = snippet.Detail,
                            CaretOffset = snippet.CaretOffset >= 0 ? Math.Max(0, snippet.CaretOffset - 1) : -1,
                            SortPriority = snippet.SortPriority,
                        });
                    }
                    else
                    {
                        items.Add(snippet);
                    }
                }

                if (prefix.Length > 0)
                    items = items.Where(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                break;
            }

            case HtmlContext.InsideTag:
            {
                // Show attributes: tag-specific first (priority 0), then global (priority 1)
                var (_, prefix) = GetWordAtCaret(text, caretPosition);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (context.TagName != null && TagAttributes.TryGetValue(context.TagName, out var specific))
                {
                    foreach (var attr in specific)
                    {
                        if (!seen.Add(attr)) continue;
                        items.Add(new CompletionItem
                        {
                            Label = attr,
                            InsertText = attr,
                            Kind = CompletionItemKind.Property,
                            SortPriority = 0,
                        });
                    }
                }

                foreach (var attr in GlobalAttributes)
                {
                    if (!seen.Add(attr)) continue;
                    items.Add(new CompletionItem
                    {
                        Label = attr,
                        InsertText = attr,
                        Kind = CompletionItemKind.Property,
                        SortPriority = 1,
                    });
                }

                if (prefix.Length > 0)
                    items = items.Where(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                break;
            }

            default:
                return items;
        }

        // Deduplicate
        items = items
            .GroupBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => x.SortPriority).First())
            .OrderBy(x => x.SortPriority)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return items.Count > 50 ? items.Take(50).ToList() : items;
    }

    public (int start, string prefix) GetWordAtCaret(string text, int caretPosition)
    {
        if (caretPosition <= 0 || caretPosition > text.Length) return (-1, "");
        int start = caretPosition - 1;
        while (start >= 0 && IsIdentifierChar(text[start]))
            start--;
        start++;
        if (start >= caretPosition) return (-1, "");
        return (start, text[start..caretPosition]);
    }

    public CompletionTrigger? GetTriggerForCharacter(char c) => c == '<' ? CompletionTrigger.Angle : null;

    public bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '-' || c == '_';

    private record struct ContextInfo(HtmlContext Context, string? TagName);

    private static ContextInfo DetermineContext(string text, int caretPosition)
    {
        // Scan backwards to find context
        int i = caretPosition - 1;

        // Skip current word
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == '_'))
            i--;

        // Skip whitespace
        while (i >= 0 && char.IsWhiteSpace(text[i]))
            i--;

        if (i < 0) return new ContextInfo(HtmlContext.Text, null);

        char c = text[i];

        // After '<'
        if (c == '<')
            return new ContextInfo(HtmlContext.AfterOpenAngle, null);

        // Inside an attribute value (after = and opening quote)
        if (c == '"' || c == '\'')
        {
            // Check if there's an = before the quote — this is an attribute value
            int j = i - 1;
            while (j >= 0 && char.IsWhiteSpace(text[j])) j--;
            if (j >= 0 && text[j] == '=')
                return new ContextInfo(HtmlContext.InsideAttributeValue, null);
        }

        // After an attribute value or after tag name — find the tag name
        // Scan backwards past attributes to find '<tagname'
        int pos = i;
        while (pos >= 0)
        {
            if (text[pos] == '<')
            {
                // Found opening angle — extract tag name
                int nameStart = pos + 1;
                if (nameStart < text.Length && text[nameStart] == '/') nameStart++;
                int nameEnd = nameStart;
                while (nameEnd < text.Length && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] == '-' || text[nameEnd] == '_'))
                    nameEnd++;
                string tagName = text[nameStart..nameEnd];
                return new ContextInfo(HtmlContext.InsideTag, tagName);
            }
            if (text[pos] == '>')
            {
                // We're outside a tag
                return new ContextInfo(HtmlContext.Text, null);
            }
            pos--;
        }

        return new ContextInfo(HtmlContext.Text, null);
    }
}
