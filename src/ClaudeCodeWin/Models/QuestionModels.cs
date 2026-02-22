using System.Collections.Generic;
using System.ComponentModel;

namespace ClaudeCodeWin.Models;

/// <summary>
/// Model for displaying AskUserQuestion in the UI.
/// </summary>
public class QuestionDisplayModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string QuestionText { get; set; } = "";
    public List<QuestionOption> Options { get; set; } = [];

    private bool _isAnswered;
    public bool IsAnswered
    {
        get => _isAnswered;
        set
        {
            if (_isAnswered == value) return;
            _isAnswered = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAnswered)));
        }
    }
}
