using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ClaudeCodeWin.Services.Highlighting;

namespace ClaudeCodeWin.Views;

public class SyntaxHighlightLayer : FrameworkElement
{
    private string _text = "";
    private List<SyntaxToken> _tokens = [];
    private int[] _lineStarts = [0];
    private (int pos, int matchPos) _bracketHighlight = (-1, -1);

    // Font metrics
    private Typeface _typeface = new("Cascadia Code");
    private double _fontSize = 14;
    private double _lineHeight;
    private double _charWidth;
    private Thickness _editorPadding = new(8);

    // Scroll state
    private double _verticalOffset;
    private double _horizontalOffset;
    private double _viewportHeight;

    // Frozen brushes for each token type
    private static readonly Brush PlainTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xe6, 0xed, 0xf3)));
    private static readonly Brush KeywordBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)));
    private static readonly Brush ControlKeywordBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0)));
    private static readonly Brush TypeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)));
    private static readonly Brush StringBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78)));
    private static readonly Brush NumberBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8)));
    private static readonly Brush CommentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55)));
    private static readonly Brush PreprocessorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));
    private static readonly Brush LiteralBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)));
    private static readonly Brush BracketMatchBg = Freeze(new SolidColorBrush(Color.FromRgb(0x2a, 0x4a, 0x2a)));
    private static readonly Brush BracketMatchBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
    private static readonly Pen BracketMatchPen = FreezePen(BracketMatchBorder, 1);

    private static Brush Freeze(SolidColorBrush brush) { brush.Freeze(); return brush; }
    private static Pen FreezePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    public void UpdateState(string text, List<SyntaxToken> tokens, int[] lineStarts,
        (int pos, int matchPos) bracketHighlight,
        double verticalOffset, double horizontalOffset, double viewportHeight)
    {
        _text = text;
        _tokens = tokens;
        _lineStarts = lineStarts;
        _bracketHighlight = bracketHighlight;
        _verticalOffset = verticalOffset;
        _horizontalOffset = horizontalOffset;
        _viewportHeight = viewportHeight;
    }

    public void SetFontMetrics(Typeface typeface, double fontSize, double lineHeight, double charWidth, Thickness padding)
    {
        _typeface = typeface;
        _fontSize = fontSize;
        _lineHeight = lineHeight;
        _charWidth = charWidth;
        _editorPadding = padding;
    }

    public void InvalidateHighlighting() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(_text) || _lineHeight <= 0 || _tokens.Count == 0)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double pixelsPerDip = dpi.PixelsPerDip;

        int totalLines = _lineStarts.Length;
        int firstLine = Math.Max(0, (int)(_verticalOffset / _lineHeight));
        int lastLine = Math.Min(totalLines - 1,
            (int)((_verticalOffset + _viewportHeight) / _lineHeight) + 1);

        for (int lineIdx = firstLine; lineIdx <= lastLine; lineIdx++)
        {
            int lineStart = _lineStarts[lineIdx];
            int lineEnd = lineIdx + 1 < totalLines
                ? _lineStarts[lineIdx + 1]
                : _text.Length;

            // Trim trailing \r\n
            int lineTextEnd = lineEnd;
            while (lineTextEnd > lineStart && (_text[lineTextEnd - 1] == '\n' || _text[lineTextEnd - 1] == '\r'))
                lineTextEnd--;

            int lineLen = lineTextEnd - lineStart;
            if (lineLen <= 0) continue;

            string lineText = _text.Substring(lineStart, lineLen);

            var ft = new FormattedText(
                lineText,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                _typeface,
                _fontSize,
                PlainTextBrush,
                pixelsPerDip);

            // Apply token colors
            ApplyTokenColors(ft, lineStart, lineTextEnd);

            // Draw bracket match backgrounds
            DrawBracketHighlights(dc, lineIdx, lineStart, lineTextEnd);

            double y = lineIdx * _lineHeight + _editorPadding.Top - _verticalOffset;
            double x = _editorPadding.Left - _horizontalOffset;

            dc.DrawText(ft, new Point(x, y));
        }
    }

    private void ApplyTokenColors(FormattedText ft, int lineStart, int lineEnd)
    {
        // Binary search for first token that could overlap this line
        int tokenIdx = FindFirstTokenIndex(lineStart);

        for (int i = tokenIdx; i < _tokens.Count; i++)
        {
            var token = _tokens[i];
            if (token.Start >= lineEnd) break;

            int tokenEnd = token.Start + token.Length;
            if (tokenEnd <= lineStart) continue;

            int startInLine = Math.Max(0, token.Start - lineStart);
            int endInLine = Math.Min(lineEnd - lineStart, tokenEnd - lineStart);
            int len = endInLine - startInLine;
            if (len <= 0) continue;

            var brush = GetTokenBrush(token.Type);
            ft.SetForegroundBrush(brush, startInLine, len);
        }
    }

    private void DrawBracketHighlights(DrawingContext dc, int lineIdx, int lineStart, int lineEnd)
    {
        if (_bracketHighlight.pos < 0 && _bracketHighlight.matchPos < 0)
            return;

        DrawBracketRect(dc, _bracketHighlight.pos, lineIdx, lineStart, lineEnd);
        DrawBracketRect(dc, _bracketHighlight.matchPos, lineIdx, lineStart, lineEnd);
    }

    private void DrawBracketRect(DrawingContext dc, int pos, int lineIdx, int lineStart, int lineEnd)
    {
        if (pos < lineStart || pos >= lineEnd) return;

        int col = pos - lineStart;
        double x = _editorPadding.Left - _horizontalOffset + col * _charWidth;
        double y = lineIdx * _lineHeight + _editorPadding.Top - _verticalOffset;

        var rect = new Rect(x, y, _charWidth, _lineHeight);
        dc.DrawRectangle(BracketMatchBg, BracketMatchPen, rect);
    }

    private int FindFirstTokenIndex(int position)
    {
        int lo = 0, hi = _tokens.Count - 1;
        int result = _tokens.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int tokenEnd = _tokens[mid].Start + _tokens[mid].Length;
            if (tokenEnd <= position)
                lo = mid + 1;
            else
            {
                result = mid;
                hi = mid - 1;
            }
        }
        return result;
    }

    private static Brush GetTokenBrush(SyntaxTokenType type) => type switch
    {
        SyntaxTokenType.Keyword => KeywordBrush,
        SyntaxTokenType.ControlKeyword => ControlKeywordBrush,
        SyntaxTokenType.TypeKeyword => TypeBrush,
        SyntaxTokenType.TypeName => TypeBrush,
        SyntaxTokenType.Literal => LiteralBrush,
        SyntaxTokenType.String => StringBrush,
        SyntaxTokenType.Number => NumberBrush,
        SyntaxTokenType.Comment => CommentBrush,
        SyntaxTokenType.Preprocessor => PreprocessorBrush,
        _ => PlainTextBrush,
    };
}
