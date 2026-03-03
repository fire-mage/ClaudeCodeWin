using ClaudeCodeWin.Infrastructure;

namespace ClaudeCodeWin.Models;

public enum SubTabType
{
    Explorer,
    Chat,
    FileEditor,
    Notepad
}

public class SubTab : ViewModelBase
{
    private string _title;
    private bool _hasUnsavedChanges;
    private bool _isActive;
    private string _content = "";
    private string _savedContent = "";
    private int _badgeCount;

    public SubTab(SubTabType type, string title, string? filePath = null)
    {
        Type = type;
        _title = title;
        FilePath = filePath;
        IsCloseable = type == SubTabType.FileEditor;
    }

    public SubTabType Type { get; }
    public string? FilePath { get; }
    public bool IsCloseable { get; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set
        {
            if (SetProperty(ref _hasUnsavedChanges, value))
                OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value))
                HasUnsavedChanges = _content != _savedContent;
        }
    }

    /// <summary>Marks current content as saved (resets unsaved indicator).</summary>
    public void MarkSaved()
    {
        _savedContent = _content;
        HasUnsavedChanges = false;
    }

    public int BadgeCount
    {
        get => _badgeCount;
        set => SetProperty(ref _badgeCount, value);
    }

    public string DisplayTitle => HasUnsavedChanges ? $"● {Title}" : Title;
}
