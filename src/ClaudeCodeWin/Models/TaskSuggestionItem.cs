namespace ClaudeCodeWin.Models;

public class TaskSuggestionItem
{
    public string Label { get; set; } = "";
    public bool IsCommit { get; set; }
    public TaskDefinition? Task { get; set; }
}
