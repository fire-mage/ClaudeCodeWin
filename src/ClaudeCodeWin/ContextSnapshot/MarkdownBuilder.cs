using System.Text;

namespace ClaudeCodeWin.ContextSnapshot;

public class MarkdownBuilder
{
    private readonly StringBuilder _sb = new();

    public MarkdownBuilder Header(int level, string text)
    {
        _sb.AppendLine();
        _sb.Append(new string('#', level));
        _sb.Append(' ');
        _sb.AppendLine(text);
        _sb.AppendLine();
        return this;
    }

    public MarkdownBuilder Line(string text = "")
    {
        _sb.AppendLine(text);
        return this;
    }

    public MarkdownBuilder Table(string[] headers, List<string[]> rows)
    {
        if (rows.Count == 0)
        {
            _sb.AppendLine("*No data*");
            _sb.AppendLine();
            return this;
        }

        _sb.Append('|');
        foreach (var h in headers)
            _sb.Append($" {h} |");
        _sb.AppendLine();

        _sb.Append('|');
        foreach (var _ in headers)
            _sb.Append(" --- |");
        _sb.AppendLine();

        foreach (var row in rows)
        {
            _sb.Append('|');
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = i < row.Length ? row[i] : "";
                _sb.Append($" {cell} |");
            }
            _sb.AppendLine();
        }

        _sb.AppendLine();
        return this;
    }

    public MarkdownBuilder CodeBlock(string content, string lang = "")
    {
        _sb.AppendLine($"```{lang}");
        _sb.AppendLine(content);
        _sb.AppendLine("```");
        _sb.AppendLine();
        return this;
    }

    public MarkdownBuilder Append(string text)
    {
        _sb.Append(text);
        return this;
    }

    public override string ToString() => _sb.ToString();
}
