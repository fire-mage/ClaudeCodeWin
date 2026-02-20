namespace ClaudeCodeWin.Models;

public class ChatHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string? ProjectPath { get; set; }
    public string? SessionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = [];
}

public class ChatHistorySummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? ProjectPath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int MessageCount { get; set; }
}
