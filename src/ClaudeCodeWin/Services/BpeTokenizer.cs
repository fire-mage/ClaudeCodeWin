using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Minimal BPE tokenizer for the all-MiniLM-L6-v2 model.
/// Reads HuggingFace tokenizer.json format (WordPiece, not full BPE — MiniLM uses WordPiece).
/// </summary>
public partial class BpeTokenizer
{
    private readonly Dictionary<string, int> _vocab = new();
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _padId;
    private readonly int _unkId;

    private BpeTokenizer(Dictionary<string, int> vocab, int clsId, int sepId, int padId, int unkId)
    {
        _vocab = vocab;
        _clsId = clsId;
        _sepId = sepId;
        _padId = padId;
        _unkId = unkId;
    }

    /// <summary>
    /// Load tokenizer from a HuggingFace tokenizer.json file.
    /// </summary>
    public static BpeTokenizer FromFile(string tokenizerJsonPath)
    {
        var json = File.ReadAllText(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Extract vocabulary from model.vocab
        var vocab = new Dictionary<string, int>();
        if (root.TryGetProperty("model", out var model) &&
            model.TryGetProperty("vocab", out var vocabObj))
        {
            foreach (var kv in vocabObj.EnumerateObject())
            {
                vocab[kv.Name] = kv.Value.GetInt32();
            }
        }

        // Extract special token IDs
        int clsId = vocab.GetValueOrDefault("[CLS]", 101);
        int sepId = vocab.GetValueOrDefault("[SEP]", 102);
        int padId = vocab.GetValueOrDefault("[PAD]", 0);
        int unkId = vocab.GetValueOrDefault("[UNK]", 100);

        return new BpeTokenizer(vocab, clsId, sepId, padId, unkId);
    }

    /// <summary>
    /// Encode text into model inputs. Returns (InputIds, AttentionMask, TokenTypeIds).
    /// All arrays have the same length = maxLength, padded as needed.
    /// Format: [CLS] tokens... [SEP] [PAD]...
    /// </summary>
    public (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Encode(string text, int maxLength = 256)
    {
        // Tokenize using WordPiece
        var tokens = WordPieceTokenize(text, maxLength - 2); // Reserve 2 for CLS/SEP

        // Build input_ids: [CLS] + tokens + [SEP] + padding
        var inputIds = new long[maxLength];
        var attentionMask = new long[maxLength];
        var tokenTypeIds = new long[maxLength]; // All zeros for single-sentence

        int pos = 0;
        inputIds[pos] = _clsId;
        attentionMask[pos] = 1;
        pos++;

        foreach (var tokenId in tokens)
        {
            if (pos >= maxLength - 1) break; // Leave room for SEP
            inputIds[pos] = tokenId;
            attentionMask[pos] = 1;
            pos++;
        }

        inputIds[pos] = _sepId;
        attentionMask[pos] = 1;
        pos++;

        // Rest is already 0 (PAD with attention_mask=0)

        return (inputIds, attentionMask, tokenTypeIds);
    }

    /// <summary>
    /// WordPiece tokenization: split text into subword tokens.
    /// </summary>
    private List<int> WordPieceTokenize(string text, int maxTokens)
    {
        var result = new List<int>();

        // Pre-tokenize: lowercase, split on whitespace and punctuation
        var normalized = text.ToLowerInvariant();
        var words = PreTokenizeRegex().Matches(normalized);

        foreach (Match wordMatch in words)
        {
            if (result.Count >= maxTokens) break;

            var word = wordMatch.Value;
            var subTokens = TokenizeWord(word);

            foreach (var tokenId in subTokens)
            {
                if (result.Count >= maxTokens) break;
                result.Add(tokenId);
            }
        }

        return result;
    }

    /// <summary>
    /// Tokenize a single word using WordPiece greedy longest-match-first.
    /// </summary>
    private List<int> TokenizeWord(string word)
    {
        var tokens = new List<int>();
        int start = 0;

        while (start < word.Length)
        {
            int end = word.Length;
            int foundId = -1;

            while (start < end)
            {
                var substr = word[start..end];
                var lookup = start > 0 ? "##" + substr : substr;

                if (_vocab.TryGetValue(lookup, out var id))
                {
                    foundId = id;
                    break;
                }
                end--;
            }

            if (foundId < 0)
            {
                // Unknown character — use [UNK]
                tokens.Add(_unkId);
                start++;
            }
            else
            {
                tokens.Add(foundId);
                start = end;
            }
        }

        return tokens;
    }

    [GeneratedRegex(@"[\w]+|[^\s\w]")]
    private static partial Regex PreTokenizeRegex();
}
