namespace ClaudeCodeWin.Models;

public enum MessageRole
{
    User,
    Assistant,
    System
}

public class ChatMessage
{
    public MessageRole Role { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public List<ToolUseInfo> ToolUses { get; set; } = [];
    public bool IsStreaming { get; set; }
}

public class ToolUseInfo
{
    public string ToolName { get; set; } = string.Empty;
    public string Input { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public bool IsExpanded { get; set; }
}
