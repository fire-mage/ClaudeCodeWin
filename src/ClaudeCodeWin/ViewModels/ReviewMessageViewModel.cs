using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public class ReviewMessageViewModel : ViewModelBase
{
    private string _text = "";
    private bool _isStreaming;

    public ReviewRole Role { get; }
    public DateTime Timestamp { get; } = DateTime.Now;

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

    public string RoleLabel => Role switch
    {
        ReviewRole.Reviewer => "Reviewer",
        ReviewRole.Driver => "Driver",
        ReviewRole.Judge => "Judge",
        ReviewRole.System => "System",
        _ => ""
    };

    public string RoleIcon => Role switch
    {
        ReviewRole.Reviewer => "\U0001F50D", // magnifying glass
        ReviewRole.Driver => "\U0001F4BB",   // laptop
        ReviewRole.Judge => "\U00002696",    // scales
        ReviewRole.System => "\U00002699",   // gear
        _ => ""
    };

    public ReviewMessageViewModel(ReviewRole role, string text = "", bool isStreaming = false)
    {
        Role = role;
        _text = text;
        _isStreaming = isStreaming;
    }
}
