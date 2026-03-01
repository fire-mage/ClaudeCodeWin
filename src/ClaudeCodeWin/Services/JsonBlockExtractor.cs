using System.Text.Json;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Shared utility for extracting JSON blocks from CLI text output.
/// Used by AnalyzerService, PlannerService, and PlanReviewerService.
/// </summary>
internal static class JsonBlockExtractor
{
    /// <summary>
    /// Extract a JSON block containing the given key from text.
    /// Tries fenced code blocks first, then raw JSON objects.
    /// </summary>
    public static string? Extract(string text, string requiredKey = "verdict")
    {
        // Try fenced code block first (search backwards to prefer the last one)
        var keyLiteral = $"\"{requiredKey}\"";
        var searchFrom = text.Length;
        while (searchFrom > 0)
        {
            var jsonFenceStart = text.LastIndexOf("```json", searchFrom - 1, StringComparison.OrdinalIgnoreCase);
            if (jsonFenceStart < 0) break;

            var contentStart = text.IndexOf('\n', jsonFenceStart);
            if (contentStart >= 0)
            {
                contentStart++;
                var fenceEnd = text.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (fenceEnd > contentStart)
                {
                    var json = text[contentStart..fenceEnd].Trim();
                    if (json.Contains(keyLiteral) && IsValidJson(json)) return json;
                }
            }

            searchFrom = jsonFenceStart;
        }

        // Try to find a raw JSON object with the required key
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] != '{') continue;

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var j = i; j < text.Length; j++)
            {
                var ch = text[j];
                if (escaped) { escaped = false; continue; }
                if (ch == '\\' && inString) { escaped = true; continue; }
                if (ch == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        var candidate = text[i..(j + 1)];
                        if (candidate.Contains(keyLiteral) && IsValidJson(candidate))
                            return candidate;
                        break;
                    }
                }
            }
        }

        return null;
    }

    public static bool IsValidJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return true;
        }
        catch { return false; }
    }
}
