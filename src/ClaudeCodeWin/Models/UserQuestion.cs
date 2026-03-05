using System.ComponentModel;

namespace ClaudeCodeWin.Models;

public class UserQuestion
{
    public string Question { get; set; } = "";
    public string Header { get; set; } = "";
    public List<QuestionOption> Options { get; set; } = [];
    public bool MultiSelect { get; set; }
}

// Fix WARNING #1: Added INotifyPropertyChanged so XAML toggle highlight works for multi-select
public class QuestionOption : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; set; } = "";
    public string Description { get; set; } = "";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
