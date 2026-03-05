using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public class PhpCompletionProvider : ICompletionProvider
{
    private static readonly HashSet<string> s_snippetLabels =
        new(PhpSnippets.All.Select(s => s.Label), StringComparer.Ordinal);

    private static readonly List<CompletionItem> s_keywordItems = BuildKeywordItems();
    private static readonly List<CompletionItem> s_builtinItems = BuildBuiltinItems();

    // PHP uses -> and :: for member access, not dot. Dot is string concatenation.
    public CompletionTrigger? GetTriggerForCharacter(char c) => null;

    public bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    public (int start, string prefix) GetWordAtCaret(string text, int caretPosition)
    {
        if (caretPosition <= 0 || caretPosition > text.Length)
            return (-1, "");
        int i = caretPosition - 1;
        while (i >= 0 && IsIdentifierChar(text[i]))
            i--;
        int start = i + 1;
        if (start >= caretPosition)
            return (-1, "");
        return (start, text[start..caretPosition]);
    }

    public List<CompletionItem> GetCompletions(string text, int caretPosition, CompletionTrigger trigger, List<SyntaxToken> tokens)
    {
        var (wordStart, prefix) = GetWordAtCaret(text, caretPosition);

        // For $-prefixed typing, only require $ + 1 char
        if (trigger == CompletionTrigger.Typing)
        {
            if (prefix.StartsWith('$'))
            {
                if (prefix.Length < 2) return [];
            }
            else if (prefix.Length < 2)
            {
                return [];
            }
        }

        var items = new List<CompletionItem>();

        // Keywords (excluding those covered by snippets)
        items.AddRange(s_keywordItems);

        // Built-in functions
        items.AddRange(s_builtinItems);

        // Snippets
        items.AddRange(PhpSnippets.All);

        // Local identifiers from file (including $variables)
        if (text.Length <= 200_000)
            items.AddRange(ExtractIdentifiers(text, tokens));

        // Filter by prefix
        if (prefix.Length > 0)
        {
            items = items.Where(item =>
                item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Deduplicate: case-sensitive because PHP variables ($foo vs $Foo) are distinct
        var seen = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);
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
        var seen = new HashSet<string>(StringComparer.Ordinal);
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
                        or SyntaxTokenType.Literal)
                    {
                        i = tok.Start + tok.Length;
                        continue;
                    }
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

            char c = text[i];

            // $variable extraction
            if (c == '$' && i + 1 < len && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                int start = i;
                i++; // skip $
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;

                var word = text[start..i];
                if (seen.Add(word))
                {
                    result.Add(new CompletionItem
                    {
                        Label = word,
                        InsertText = word,
                        Kind = CompletionItemKind.Identifier,
                        SortPriority = 1,
                    });
                }
                continue;
            }

            // Regular identifiers
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;

                if (i - start >= 2)
                {
                    var word = text[start..i];
                    if (!PhpTokenizer.Keywords.Contains(word) &&
                        !PhpTokenizer.ControlKeywords.Contains(word) &&
                        !PhpTokenizer.TypeKeywords.Contains(word) &&
                        !PhpTokenizer.LiteralKeywords.Contains(word) &&
                        !PhpTokenizer.BuiltinFunctions.Contains(word) &&
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
        var allKeywords = new HashSet<string>(PhpTokenizer.Keywords);
        // BUG FIX: ControlKeywords (break, continue, return, etc.) were missing from completions
        allKeywords.UnionWith(PhpTokenizer.ControlKeywords);
        allKeywords.UnionWith(PhpTokenizer.TypeKeywords);
        allKeywords.UnionWith(PhpTokenizer.LiteralKeywords);

        var items = new List<CompletionItem>();
        foreach (var kw in allKeywords)
        {
            if (s_snippetLabels.Contains(kw))
                continue;

            var kind = PhpTokenizer.TypeKeywords.Contains(kw)
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

    private static List<CompletionItem> BuildBuiltinItems()
    {
        var items = new List<CompletionItem>();
        foreach (var fn in PhpTokenizer.BuiltinFunctions)
        {
            if (s_snippetLabels.Contains(fn))
                continue;

            items.Add(new CompletionItem
            {
                Label = fn,
                InsertText = fn,
                Kind = CompletionItemKind.Keyword,
                Detail = "built-in",
                SortPriority = 2,
            });
        }
        return items;
    }
}
