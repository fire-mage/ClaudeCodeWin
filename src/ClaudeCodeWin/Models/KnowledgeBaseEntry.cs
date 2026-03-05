namespace ClaudeCodeWin.Models;

public class KnowledgeBaseEntry
{
    public string Id { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Source { get; set; } = "claude"; // "claude" or "user"
    public List<string> Tags { get; set; } = [];
    public string WhenToRead { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;

    // Fix: null-safe — Tags can be null when deserialized from JSON with "tags": null
    public string TagsDisplay => Tags is { Count: > 0 } ? string.Join(", ", Tags) : "";
}
