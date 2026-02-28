using System.Windows.Media;

namespace ClaudeCodeWin.Models;

public enum CompletionItemKind
{
    Keyword,
    TypeKeyword,
    Identifier,
    Snippet,
    Tag,
    Property,
}

public class CompletionItem
{
    public required string Label { get; init; }
    public required string InsertText { get; init; }
    public required CompletionItemKind Kind { get; init; }
    public string? Detail { get; init; }
    public int CaretOffset { get; init; } = -1; // -1 = end of insert; >=0 = offset from start
    public int SortPriority { get; init; }

    // Badge text for the popup UI
    public string KindBadge => Kind switch
    {
        CompletionItemKind.Keyword => "Kw",
        CompletionItemKind.TypeKeyword => "Tp",
        CompletionItemKind.Identifier => "Id",
        CompletionItemKind.Snippet => "Sn",
        CompletionItemKind.Tag => "Tg",
        CompletionItemKind.Property => "Pr",
        _ => "??",
    };

    // Badge color for the popup UI
    public Brush KindBrush => Kind switch
    {
        CompletionItemKind.Keyword => s_keywordBrush,
        CompletionItemKind.TypeKeyword => s_typeBrush,
        CompletionItemKind.Identifier => s_identifierBrush,
        CompletionItemKind.Snippet => s_snippetBrush,
        CompletionItemKind.Tag => s_tagBrush,
        CompletionItemKind.Property => s_propertyBrush,
        _ => s_identifierBrush,
    };

    private static readonly SolidColorBrush s_keywordBrush = Freeze(Color.FromRgb(0x56, 0x9C, 0xD6));
    private static readonly SolidColorBrush s_typeBrush = Freeze(Color.FromRgb(0x4E, 0xC9, 0xB0));
    private static readonly SolidColorBrush s_identifierBrush = Freeze(Color.FromRgb(0xe6, 0xed, 0xf3));
    private static readonly SolidColorBrush s_snippetBrush = Freeze(Color.FromRgb(0xC5, 0x86, 0xC0));
    private static readonly SolidColorBrush s_tagBrush = Freeze(Color.FromRgb(0x56, 0x9C, 0xD6));
    private static readonly SolidColorBrush s_propertyBrush = Freeze(Color.FromRgb(0x9C, 0xDC, 0xFE));

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
