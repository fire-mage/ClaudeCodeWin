using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public class SqlCompletionProvider : ICompletionProvider
{
    private static readonly HashSet<string> s_snippetLabels =
        new(SqlSnippets.All.Select(s => s.Label), StringComparer.OrdinalIgnoreCase);

    private static readonly List<CompletionItem> s_keywordItems = BuildKeywordItems();
    private static readonly List<CompletionItem> s_builtinItems = BuildBuiltinItems();

    private static readonly HashSet<string> s_builtinFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "CAST", "CONVERT",
        "CONCAT", "SUBSTRING", "TRIM", "UPPER", "LOWER", "LENGTH", "ROUND",
        "GETDATE", "NOW", "ISNULL", "NULLIF", "ROW_NUMBER", "RANK",
        "DENSE_RANK", "LEAD", "LAG", "FIRST_VALUE", "LAST_VALUE",
    };

    public (int start, string prefix) GetWordAtCaret(string text, int caretPosition)
    {
        if (caretPosition <= 0 || caretPosition > text.Length)
            return (-1, "");
        int i = caretPosition - 1;
        // Include @ and : as valid start characters for SQL variables/bind params
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '@' || text[i] == ':'))
            i--;
        int start = i + 1;
        if (start >= caretPosition)
            return (-1, "");
        return (start, text[start..caretPosition]);
    }

    public bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == ':';

    public List<CompletionItem> GetCompletions(string text, int caretPosition, CompletionTrigger trigger, List<SyntaxToken> tokens)
    {
        var (wordStart, prefix) = GetWordAtCaret(text, caretPosition);

        if (trigger == CompletionTrigger.Dot)
        {
            var identifiers = ExtractIdentifiers(text, tokens);
            identifiers.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            return identifiers.Count > 50 ? identifiers.GetRange(0, 50) : identifiers;
        }

        if (trigger == CompletionTrigger.Typing && prefix.Length < 2)
            return [];

        var items = new List<CompletionItem>();

        // Keywords (excluding those covered by snippets)
        items.AddRange(s_keywordItems);

        // Built-in functions
        items.AddRange(s_builtinItems);

        // Snippets
        items.AddRange(SqlSnippets.All);

        // Local identifiers from file
        if (text.Length <= 200_000)
            items.AddRange(ExtractIdentifiers(text, tokens));

        // Filter by prefix (case-insensitive for SQL)
        if (prefix.Length > 0)
        {
            items = items.Where(item =>
                item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Deduplicate: prefer lower SortPriority (snippet=0 > identifier=1 > keyword=2)
        var seen = new Dictionary<string, CompletionItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!seen.TryGetValue(item.Label, out var existing) || item.SortPriority < existing.SortPriority)
                seen[item.Label] = item;
        }

        var result = seen.Values.ToList();
        result.Sort((a, b) =>
        {
            int cmp = a.SortPriority.CompareTo(b.SortPriority);
            return cmp != 0 ? cmp : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });

        return result.Count > 50 ? result.GetRange(0, 50) : result;
    }

    private static List<CompletionItem> ExtractIdentifiers(string text, List<SyntaxToken> tokens)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CompletionItem>();

        int tokenIdx = 0;
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            while (tokenIdx < tokens.Count && tokens[tokenIdx].Start + tokens[tokenIdx].Length <= i)
                tokenIdx++;

            if (tokenIdx < tokens.Count)
            {
                var tok = tokens[tokenIdx];
                if (i >= tok.Start && i < tok.Start + tok.Length)
                {
                    if (tok.Type is SyntaxTokenType.String or SyntaxTokenType.Comment or SyntaxTokenType.Preprocessor)
                    {
                        i = tok.Start + tok.Length;
                        continue;
                    }
                    if (tok.Type is SyntaxTokenType.Keyword or SyntaxTokenType.ControlKeyword
                        or SyntaxTokenType.TypeKeyword or SyntaxTokenType.Literal)
                    {
                        i = tok.Start + tok.Length;
                        continue;
                    }
                }
            }

            char c = text[i];
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;

                if (i - start >= 2)
                {
                    var word = text[start..i];
                    if (!SqlTokenizer.Keywords.Contains(word) &&
                        !SqlTokenizer.ControlKeywords.Contains(word) &&
                        !SqlTokenizer.TypeKeywords.Contains(word) &&
                        !SqlTokenizer.LiteralKeywords.Contains(word) &&
                        !s_builtinFunctions.Contains(word) &&
                        seen.Add(word))
                    {
                        result.Add(new CompletionItem
                        {
                            Label = word,
                            InsertText = word,
                            Kind = CompletionItemKind.Identifier,
                            SortPriority = 1,
                        });
                    }
                }
                continue;
            }

            i++;
        }

        return result;
    }

    private static List<CompletionItem> BuildKeywordItems()
    {
        var allKeywords = new HashSet<string>(SqlTokenizer.Keywords, StringComparer.OrdinalIgnoreCase);
        // BUG FIX: ControlKeywords (BEGIN, END, IF, etc.) were missing from completions
        allKeywords.UnionWith(SqlTokenizer.ControlKeywords);
        allKeywords.UnionWith(SqlTokenizer.TypeKeywords);
        allKeywords.UnionWith(SqlTokenizer.LiteralKeywords);

        var items = new List<CompletionItem>();
        foreach (var kw in allKeywords)
        {
            // Use UPPERCASE for SQL keyword display
            var upper = kw.ToUpperInvariant();
            if (s_snippetLabels.Contains(upper))
                continue;

            var kind = SqlTokenizer.TypeKeywords.Contains(kw)
                ? CompletionItemKind.TypeKeyword
                : CompletionItemKind.Keyword;

            items.Add(new CompletionItem
            {
                Label = upper,
                InsertText = upper,
                Kind = kind,
                SortPriority = 2,
            });
        }
        return items;
    }

    private static List<CompletionItem> BuildBuiltinItems()
    {
        var items = new List<CompletionItem>();
        foreach (var fn in s_builtinFunctions)
        {
            var upper = fn.ToUpperInvariant();
            if (s_snippetLabels.Contains(upper))
                continue;

            items.Add(new CompletionItem
            {
                Label = upper,
                InsertText = upper,
                Kind = CompletionItemKind.Keyword,
                Detail = "function",
                SortPriority = 2,
            });
        }
        return items;
    }
}
