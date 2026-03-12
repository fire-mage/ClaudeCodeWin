namespace ClaudeCodeWin.Models;

public class VectorSearchResult
{
    public string Id { get; set; } = "";
    public string SourceType { get; set; } = "";     // "kb", "chat", "notepad", "memory"
    public string SourceId { get; set; } = "";
    public string Text { get; set; } = "";
    public float Similarity { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}
