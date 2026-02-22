using ClaudeCodeWin.Infrastructure;

namespace ClaudeCodeWin.Models;

public class TaskSuggestionItem : ViewModelBase
{
    private bool _isCompleted;
    private string? _completedStatusText;

    public string Label { get; set; } = "";
    public bool IsCommit { get; set; }
    public TaskDefinition? Task { get; set; }

    public bool IsCompleted
    {
        get => _isCompleted;
        set => SetProperty(ref _isCompleted, value);
    }

    public string? CompletedStatusText
    {
        get => _completedStatusText;
        set => SetProperty(ref _completedStatusText, value);
    }
}
