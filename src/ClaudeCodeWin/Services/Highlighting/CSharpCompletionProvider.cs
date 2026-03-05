using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public class CSharpCompletionProvider : ICompletionProvider
{
    // All keyword names that snippets also cover — snippets take priority
    private static readonly HashSet<string> s_snippetLabels =
        new(CSharpSnippets.All.Select(s => s.Label), StringComparer.Ordinal);

    private static readonly List<CompletionItem> s_keywordItems = BuildKeywordItems();

    public (int start, string prefix) GetWordAtCaret(string text, int caretPosition)
    {
        if (caretPosition <= 0 || caretPosition > text.Length)
            return (-1, "");
        int i = caretPosition - 1;
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            i--;
        int start = i + 1;
        if (start >= caretPosition)
            return (-1, "");
        return (start, text[start..caretPosition]);
    }

    public List<CompletionItem> GetCompletions(string text, int caretPosition, CompletionTrigger trigger, List<SyntaxToken> tokens)
    {
        var (wordStart, prefix) = GetWordAtCaret(text, caretPosition);

        if (trigger == CompletionTrigger.Dot)
        {
            // After dot: only identifiers, no prefix filter yet
            var identifiers = ExtractIdentifiers(text, tokens);
            identifiers.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            return identifiers.Count > 50 ? identifiers.GetRange(0, 50) : identifiers;
        }

        if (trigger == CompletionTrigger.Typing && prefix.Length < 2)
            return [];

        var items = new List<CompletionItem>();

        // Keywords (excluding those covered by snippets)
        items.AddRange(s_keywordItems);

        // Snippets
        items.AddRange(CSharpSnippets.All);

        // Local identifiers from file
        if (text.Length <= 200_000)
            items.AddRange(ExtractIdentifiers(text, tokens));

        // Filter by prefix
        if (prefix.Length > 0)
        {
            items = items.Where(item =>
                item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Deduplicate: prefer lower SortPriority (snippet=0 > keyword=2)
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

    /// <summary>
    /// Extracts identifiers from text using cached syntax tokens to correctly
    /// skip strings, comments, and other non-code regions.
    /// </summary>
    private static List<CompletionItem> ExtractIdentifiers(string text, List<SyntaxToken> tokens)
    {
        // Build a set of regions to skip (strings, comments, preprocessor directives)
        // Then scan text for identifiers only in code regions
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CompletionItem>();

        // Collect all token-covered ranges sorted by start position
        // Tokens are already sorted by start position from the tokenizer
        int tokenIdx = 0;
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Advance token index to find any token covering position i
            while (tokenIdx < tokens.Count && tokens[tokenIdx].Start + tokens[tokenIdx].Length <= i)
                tokenIdx++;

            // If current position is inside a string/comment/preprocessor token, skip it
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
                    // Keyword/type/literal tokens — skip (we already have them from static lists)
                    if (tok.Type is SyntaxTokenType.Keyword or SyntaxTokenType.ControlKeyword
                        or SyntaxTokenType.TypeKeyword or SyntaxTokenType.Literal)
                    {
                        i = tok.Start + tok.Length;
                        continue;
                    }
                    // TypeName tokens — these are user identifiers, collect them
                    if (tok.Type == SyntaxTokenType.TypeName)
                    {
                        var word = text[tok.Start..(tok.Start + tok.Length)];
                        if (word.Length >= 2 && seen.Add(word))
                        {
                            result.Add(new CompletionItem
                            {
                                Label = word,
                                InsertText = word,
                                Kind = CompletionItemKind.Identifier,
                                SortPriority = 1,
                            });
                        }
                        i = tok.Start + tok.Length;
                        continue;
                    }
                }
            }

            // Scan for identifiers in plain text regions (not covered by any token)
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
                    if (!CSharpTokenizer.Keywords.Contains(word) &&
                        !CSharpTokenizer.ControlKeywords.Contains(word) &&
                        !CSharpTokenizer.TypeKeywords.Contains(word) &&
                        !CSharpTokenizer.LiteralKeywords.Contains(word) &&
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
        var allKeywords = new HashSet<string>(CSharpTokenizer.Keywords);
        // BUG FIX: ControlKeywords (break, continue, return, etc.) were missing from completions
        allKeywords.UnionWith(CSharpTokenizer.ControlKeywords);
        allKeywords.UnionWith(CSharpTokenizer.TypeKeywords);
        allKeywords.UnionWith(CSharpTokenizer.LiteralKeywords);

        var items = new List<CompletionItem>();
        foreach (var kw in allKeywords)
        {
            // Skip keywords that have a snippet equivalent
            if (s_snippetLabels.Contains(kw))
                continue;

            var kind = CSharpTokenizer.TypeKeywords.Contains(kw)
                ? CompletionItemKind.TypeKeyword
                : CompletionItemKind.Keyword;

            items.Add(new CompletionItem
            {
                Label = kw,
                InsertText = kw,
                Kind = kind,
                SortPriority = 2,
            });
        }
        return items;
    }
}
