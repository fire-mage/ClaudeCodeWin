using System.Collections.ObjectModel;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public class QuestionViewModel : ViewModelBase
{
    private bool _isAnswered;

    public string QuestionText { get; }
    public string Header { get; }
    public bool MultiSelect { get; }
    public ObservableCollection<QuestionOptionViewModel> Options { get; } = [];

    public bool IsAnswered
    {
        get => _isAnswered;
        set => SetProperty(ref _isAnswered, value);
    }

    public event Action<QuestionViewModel, string>? OnAnswered;

    public QuestionViewModel(UserQuestion question)
    {
        QuestionText = question.Question;
        Header = question.Header;
        MultiSelect = question.MultiSelect;

        foreach (var opt in question.Options)
        {
            var optVm = new QuestionOptionViewModel(opt.Label, opt.Description);
            optVm.SelectCommand = new RelayCommand(() => SelectOption(optVm));
            Options.Add(optVm);
        }

        // Add "Other" option
        var otherVm = new QuestionOptionViewModel("Other", "Provide custom text input");
        otherVm.SelectCommand = new RelayCommand(() => SelectOption(otherVm));
        Options.Add(otherVm);
    }

    // Fix: MultiSelect mode was broken — first click set IsAnswered=true, blocking further selections
    private RelayCommand? _confirmMultiSelectCommand;
    public RelayCommand ConfirmMultiSelectCommand => _confirmMultiSelectCommand ??= new RelayCommand(ConfirmMultiSelect);

    private void SelectOption(QuestionOptionViewModel option)
    {
        if (IsAnswered) return;

        if (MultiSelect)
        {
            // Toggle selection; user must confirm with ConfirmMultiSelectCommand
            option.IsSelected = !option.IsSelected;
            return;
        }

        IsAnswered = true;
        option.IsSelected = true;

        // NOTE: "Other" sends a placeholder — the CLI interprets this as a free-text signal.
        // Custom text collection would require a TextBox in the UI (not yet implemented).
        var answerText = option.Label == "Other"
            ? $"Other: (custom answer for \"{Header}\")"
            : option.Label;

        OnAnswered?.Invoke(this, answerText);
    }

    private void ConfirmMultiSelect()
    {
        if (IsAnswered) return;

        var selected = Options.Where(o => o.IsSelected).ToList();
        if (selected.Count == 0) return;

        IsAnswered = true;
        // Fix: "Other" option needs same special-case handling as in single-select mode
        var labels = selected.Select(o => o.Label == "Other"
            ? $"Other: (custom answer for \"{Header}\")"
            : o.Label);
        var answerText = string.Join(", ", labels);
        OnAnswered?.Invoke(this, answerText);
    }
}

public class QuestionOptionViewModel : ViewModelBase
{
    private bool _isSelected;

    public string Label { get; }
    public string Description { get; }
    public RelayCommand SelectCommand { get; set; } = null!;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public QuestionOptionViewModel(string label, string description)
    {
        Label = label;
        Description = description;
    }
}
