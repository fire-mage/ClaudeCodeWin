namespace ClaudeCodeWin.Models;

public record ResultData(
    string? SessionId,
    string? Model,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheCreationTokens
);
