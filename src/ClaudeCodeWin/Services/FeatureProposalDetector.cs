using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Detects ```team-task fenced code blocks in assistant messages.
/// Each block contains JSON with a task to add to the Team pipeline.
/// </summary>
public static partial class FeatureProposalDetector
{
    public record FeatureProposal(string RawIdea, int Priority = 100);

    [GeneratedRegex(@"```team-task\s*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex TeamTaskBlock();

    /// <summary>
    /// Extracts team-task proposals from message text.
    /// Returns cleaned text (blocks removed) and list of parsed proposals.
    /// </summary>
    public static (string CleanedText, List<FeatureProposal> Proposals) Extract(string text)
    {
        if (string.IsNullOrEmpty(text))
            return (text ?? "", []);

        var proposals = new List<FeatureProposal>();
        var cleaned = TeamTaskBlock().Replace(text, match =>
        {
            var json = match.Groups[1].Value.Trim();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("rawIdea", out var rawIdeaEl))
                    return match.Value;

                var rawIdea = rawIdeaEl.GetString();
                if (string.IsNullOrWhiteSpace(rawIdea))
                    return match.Value;

                var priority = root.TryGetProperty("priority", out var p)
                    && p.ValueKind == JsonValueKind.Number
                        ? p.GetInt32()
                        : 100;
                proposals.Add(new FeatureProposal(rawIdea, priority));
            }
            catch (JsonException)
            {
                return match.Value;
            }

            return "";
        });

        if (proposals.Count == 0)
            return (text, []);

        return (cleaned.Trim(), proposals);
    }
}
