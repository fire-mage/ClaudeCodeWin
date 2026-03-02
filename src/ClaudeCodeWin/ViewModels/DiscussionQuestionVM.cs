using ClaudeCodeWin.Infrastructure;

namespace ClaudeCodeWin.ViewModels;

public class DiscussionQuestionVM : ViewModelBase
{
    private string? _selectedAnswer;
    private string _customAnswer = "";
    private bool _isCustom;

    public int Index { get; set; }
    public string GroupId { get; set; } = "";
    public string Question { get; set; } = "";
    public List<string> SuggestedAnswers { get; set; } = [];

    public string? SelectedAnswer
    {
        get => _selectedAnswer;
        set => SetProperty(ref _selectedAnswer, value);
    }

    public string CustomAnswer
    {
        get => _customAnswer;
        set => SetProperty(ref _customAnswer, value);
    }

    public bool IsCustom
    {
        get => _isCustom;
        set => SetProperty(ref _isCustom, value);
    }

    public string EffectiveAnswer => IsCustom ? CustomAnswer : (SelectedAnswer ?? "");
}
