namespace ClaudeCodeWin.Models;

public record ResultData(
    string? SessionId,
    string? Model,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheCreationTokens,
    int ContextWindow,
    // Per-call usage from the last message_start event in the turn.
    // Represents the actual current conversation size (not aggregated across tool-use cycles).
    int LastCallInputTokens = 0,
    int LastCallCacheReadTokens = 0,
    int LastCallCacheCreationTokens = 0,
    int LastCallOutputTokens = 0
);
