using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public class CssCompletionProvider : ICompletionProvider
{
    private static readonly string[] Properties =
    [
        // Layout
        "display", "position", "top", "right", "bottom", "left", "float", "clear",
        "z-index", "overflow", "overflow-x", "overflow-y", "visibility", "opacity",
        "box-sizing", "object-fit", "object-position",
        // Flexbox
        "flex", "flex-direction", "flex-wrap", "flex-flow", "flex-grow", "flex-shrink", "flex-basis",
        "justify-content", "align-items", "align-self", "align-content", "order", "gap", "row-gap", "column-gap",
        // Grid
        "grid", "grid-template", "grid-template-columns", "grid-template-rows", "grid-template-areas",
        "grid-column", "grid-row", "grid-column-start", "grid-column-end", "grid-row-start", "grid-row-end",
        "grid-area", "grid-auto-columns", "grid-auto-rows", "grid-auto-flow", "place-items", "place-content", "place-self",
        // Spacing
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left", "margin-inline", "margin-block",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left", "padding-inline", "padding-block",
        // Sizing
        "width", "height", "min-width", "max-width", "min-height", "max-height",
        "inline-size", "block-size", "min-inline-size", "max-inline-size", "min-block-size", "max-block-size",
        "aspect-ratio",
        // Typography
        "font", "font-family", "font-size", "font-weight", "font-style", "font-variant",
        "line-height", "letter-spacing", "word-spacing", "text-align", "text-decoration",
        "text-transform", "text-indent", "text-overflow", "text-shadow", "white-space", "word-break", "word-wrap",
        "overflow-wrap", "hyphens", "direction", "writing-mode",
        // Color & Background
        "color", "background", "background-color", "background-image", "background-position",
        "background-size", "background-repeat", "background-attachment", "background-clip", "background-origin",
        // Border
        "border", "border-top", "border-right", "border-bottom", "border-left",
        "border-width", "border-style", "border-color", "border-radius",
        "border-top-left-radius", "border-top-right-radius", "border-bottom-left-radius", "border-bottom-right-radius",
        "border-collapse", "border-spacing", "outline", "outline-width", "outline-style", "outline-color", "outline-offset",
        // Effects
        "box-shadow", "filter", "backdrop-filter", "mix-blend-mode", "clip-path",
        // Transform & Animation
        "transform", "transform-origin", "transition", "transition-property", "transition-duration",
        "transition-timing-function", "transition-delay", "animation", "animation-name", "animation-duration",
        "animation-timing-function", "animation-delay", "animation-iteration-count", "animation-direction",
        "animation-fill-mode", "animation-play-state",
        // Misc
        "cursor", "pointer-events", "user-select", "resize", "appearance",
        "list-style", "list-style-type", "list-style-position", "list-style-image",
        "content", "counter-reset", "counter-increment",
        "scroll-behavior", "scroll-snap-type", "scroll-snap-align",
        "will-change", "contain", "container-type", "container-name",
    ];

    // Context-dependent value suggestions: property → values
    private static readonly Dictionary<string, string[]> PropertyValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["display"] = ["block", "inline", "inline-block", "flex", "inline-flex", "grid", "inline-grid", "none", "contents", "table", "list-item"],
        ["position"] = ["static", "relative", "absolute", "fixed", "sticky"],
        ["flex-direction"] = ["row", "row-reverse", "column", "column-reverse"],
        ["flex-wrap"] = ["nowrap", "wrap", "wrap-reverse"],
        ["justify-content"] = ["flex-start", "flex-end", "center", "space-between", "space-around", "space-evenly", "start", "end"],
        ["align-items"] = ["stretch", "flex-start", "flex-end", "center", "baseline", "start", "end"],
        ["align-self"] = ["auto", "stretch", "flex-start", "flex-end", "center", "baseline"],
        ["overflow"] = ["visible", "hidden", "scroll", "auto", "clip"],
        ["visibility"] = ["visible", "hidden", "collapse"],
        ["text-align"] = ["left", "right", "center", "justify", "start", "end"],
        ["text-decoration"] = ["none", "underline", "overline", "line-through"],
        ["text-transform"] = ["none", "uppercase", "lowercase", "capitalize"],
        ["white-space"] = ["normal", "nowrap", "pre", "pre-wrap", "pre-line", "break-spaces"],
        ["font-weight"] = ["normal", "bold", "bolder", "lighter", "100", "200", "300", "400", "500", "600", "700", "800", "900"],
        ["font-style"] = ["normal", "italic", "oblique"],
        ["cursor"] = ["auto", "default", "pointer", "text", "move", "not-allowed", "crosshair", "grab", "grabbing", "wait", "help"],
        ["background-repeat"] = ["repeat", "no-repeat", "repeat-x", "repeat-y", "space", "round"],
        ["background-size"] = ["auto", "cover", "contain"],
        ["border-style"] = ["none", "solid", "dashed", "dotted", "double", "groove", "ridge", "inset", "outset"],
        ["box-sizing"] = ["content-box", "border-box"],
        ["list-style-type"] = ["none", "disc", "circle", "square", "decimal", "lower-alpha", "upper-alpha", "lower-roman", "upper-roman"],
        ["resize"] = ["none", "both", "horizontal", "vertical"],
        ["word-break"] = ["normal", "break-all", "keep-all", "break-word"],
        ["object-fit"] = ["fill", "contain", "cover", "none", "scale-down"],
        ["grid-auto-flow"] = ["row", "column", "dense"],
        ["animation-fill-mode"] = ["none", "forwards", "backwards", "both"],
        ["animation-direction"] = ["normal", "reverse", "alternate", "alternate-reverse"],
        ["scroll-behavior"] = ["auto", "smooth"],
        ["user-select"] = ["auto", "none", "text", "all"],
        ["pointer-events"] = ["auto", "none"],
    };

    // Snippet labels to exclude from property completions
    private static readonly HashSet<string> SnippetLabels = new(CssSnippets.Items.Select(s => s.Label));

    public List<CompletionItem> GetCompletions(string text, int caretPosition, CompletionTrigger trigger, List<SyntaxToken> tokens)
    {
        var (wordStart, prefix) = GetWordAtCaret(text, caretPosition);

        // Determine context: are we after a colon (value position)?
        var context = DetermineContext(text, caretPosition);

        var items = new List<CompletionItem>();

        if (context.IsValue && context.PropertyName != null)
        {
            // Show values for the current property
            if (PropertyValues.TryGetValue(context.PropertyName, out var values))
            {
                foreach (var val in values)
                {
                    items.Add(new CompletionItem
                    {
                        Label = val,
                        InsertText = val,
                        Kind = CompletionItemKind.Keyword,
                        SortPriority = 1,
                    });
                }
            }
            // Also add common values
            foreach (var val in new[] { "inherit", "initial", "unset", "revert" })
            {
                items.Add(new CompletionItem
                {
                    Label = val,
                    InsertText = val,
                    Kind = CompletionItemKind.Keyword,
                    SortPriority = 2,
                });
            }
        }
        else
        {
            // Show properties
            foreach (var prop in Properties)
            {
                if (SnippetLabels.Contains(prop)) continue;
                items.Add(new CompletionItem
                {
                    Label = prop,
                    InsertText = prop,
                    Kind = CompletionItemKind.Property,
                    SortPriority = 1,
                });
            }

            // Add snippets
            items.AddRange(CssSnippets.Items);
        }

        // Filter by prefix
        if (prefix.Length > 0)
        {
            items = items.Where(item => item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Deduplicate (prefer lower SortPriority)
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

    public CompletionTrigger? GetTriggerForCharacter(char c) => null; // No special trigger chars for CSS

    public bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '-' || c == '_';

    private record struct CssContext(bool IsValue, string? PropertyName);

    private static CssContext DetermineContext(string text, int caretPosition)
    {
        // Scan backwards from caret to find context
        int i = caretPosition - 1;

        // Skip current word
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == '_'))
            i--;

        // Skip whitespace
        while (i >= 0 && char.IsWhiteSpace(text[i]))
            i--;

        if (i >= 0 && text[i] == ':')
        {
            // Verify we're inside a declaration block (after '{' or ';'), not in a selector pseudo-class
            int k = i - 1;
            while (k >= 0 && text[k] != '{' && text[k] != '}' && text[k] != ';')
                k--;
            if (k < 0 || text[k] == '}')
                return new CssContext(false, null); // In a selector, not a declaration

            // We're after a colon inside a declaration block — value context. Find property name.
            i--;
            while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
            int end = i + 1;
            while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == '_'))
                i--;
            i++;
            if (i < end)
            {
                return new CssContext(true, text[i..end]);
            }
            return new CssContext(true, null);
        }

        return new CssContext(false, null);
    }
}
