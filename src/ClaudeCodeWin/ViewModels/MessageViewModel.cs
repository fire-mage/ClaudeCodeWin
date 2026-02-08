using System.Collections.ObjectModel;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public class MessageViewModel : ViewModelBase
{
    private string _text = string.Empty;
    private bool _isStreaming;
    private bool _isThinking;

    public MessageRole Role { get; }
    public DateTime Timestamp { get; }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    public bool IsThinking
    {
        get => _isThinking;
        set => SetProperty(ref _isThinking, value);
    }

    public ObservableCollection<ToolUseViewModel> ToolUses { get; } = [];
    public ObservableCollection<QuestionViewModel> Questions { get; } = [];

    public bool HasQuestions => Questions.Count > 0;

    public MessageViewModel(MessageRole role, string text = "")
    {
        Role = role;
        Text = text;
        Timestamp = DateTime.Now;
        Questions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQuestions));
    }
}

public class ToolUseViewModel : ViewModelBase
{
    private string _output = string.Empty;
    private bool _isExpanded;

    public string ToolName { get; }
    public string Input { get; }

    public string Output
    {
        get => _output;
        set => SetProperty(ref _output, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public RelayCommand ToggleExpandedCommand { get; }

    public ToolUseViewModel(string toolName, string input)
    {
        ToolName = toolName;
        Input = input;
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }
}
