using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public class MessageViewModel : ViewModelBase
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    private static readonly Regex FilePathRegex = new(
        @"(?:[A-Za-z]:\\[^\s""<>|*?]+|/[^\s""<>|*?]+)\.(?:png|jpg|jpeg|gif|bmp|webp)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Completion markers used to detect task completion in multiple languages.
    /// </summary>
    public static readonly string[] CompletionMarkers =
    [
        "готово", "done", "terminé", "fertig", "listo", "pronto",
        "выводы", "результат", "completed", "finished", "完了", "完成"
    ];

    private string _text = string.Empty;
    private bool _isStreaming;
    private bool _isThinking;
    private string _thinkingDurationText = "0s";
    private DispatcherTimer? _thinkingTimer;
    private DateTime _thinkingStartTime;
    private string _toolActivitySummary = string.Empty;
    private bool _isBookmarked;
    private string? _taskOutputText;
    private bool _isTaskOutputSent;
    private string? _completionSummary;

    public MessageRole Role { get; }
    public DateTime Timestamp { get; }

    /// <summary>
    /// Image file paths detected in the message text, displayed inline.
    /// </summary>
    public ObservableCollection<string> InlineImages { get; } = [];
    public bool HasInlineImages => InlineImages.Count > 0;

    public string Text
    {
        get => _text;
        set
        {
            SetProperty(ref _text, value);
            DetectImagePaths(value);
        }
    }

    private void DetectImagePaths(string text)
    {
        if (string.IsNullOrEmpty(text) || Role != MessageRole.Assistant) return;

        foreach (Match match in FilePathRegex.Matches(text))
        {
            var path = match.Value;
            if (File.Exists(path) && !InlineImages.Contains(path))
            {
                InlineImages.Add(path);
                OnPropertyChanged(nameof(HasInlineImages));
            }
        }
    }

    /// <summary>
    /// Add an inline image from a base64 data string saved to a temp file.
    /// </summary>
    public void AddInlineImage(string filePath)
    {
        if (!InlineImages.Contains(filePath))
        {
            InlineImages.Add(filePath);
            OnPropertyChanged(nameof(HasInlineImages));
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    public bool IsThinking
    {
        get => _isThinking;
        set
        {
            if (SetProperty(ref _isThinking, value))
            {
                if (value)
                    StartThinkingTimer();
                else
                    StopThinkingTimer();
            }
        }
    }

    public string ThinkingDurationText
    {
        get => _thinkingDurationText;
        private set => SetProperty(ref _thinkingDurationText, value);
    }

    private void StartThinkingTimer()
    {
        _thinkingStartTime = DateTime.UtcNow;
        ThinkingDurationText = "0s";
        _thinkingTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _thinkingTimer.Tick += ThinkingTimer_Tick;
        _thinkingTimer.Start();
    }

    private void StopThinkingTimer()
    {
        if (_thinkingTimer is not null)
        {
            _thinkingTimer.Stop();
            _thinkingTimer.Tick -= ThinkingTimer_Tick;
        }
    }

    private void ThinkingTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _thinkingStartTime;
        ThinkingDurationText = elapsed.TotalSeconds < 60
            ? $"{(int)elapsed.TotalSeconds}s"
            : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";
    }

    public ObservableCollection<ToolUseViewModel> ToolUses { get; } = [];

    public bool HasToolUses => ToolUses.Count > 0;

    public string ToolActivitySummary
    {
        get => _toolActivitySummary;
        private set => SetProperty(ref _toolActivitySummary, value);
    }

    /// <summary>
    /// Attachments sent with this message (screenshots, files).
    /// </summary>
    public List<FileAttachment> Attachments { get; set; } = [];
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>
    /// When non-null, this message shows a question with clickable option buttons.
    /// </summary>
    private QuestionDisplayModel? _questionDisplay;
    public QuestionDisplayModel? QuestionDisplay
    {
        get => _questionDisplay;
        set
        {
            _questionDisplay = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQuestion));
        }
    }
    public bool HasQuestion => QuestionDisplay is not null;

    /// <summary>
    /// Full task console output (for sending to Claude).
    /// </summary>
    public string? TaskOutputFull { get; set; }

    /// <summary>
    /// Collapsible task console output attached to this message (truncated for UI display).
    /// </summary>
    public string? TaskOutputText
    {
        get => _taskOutputText;
        set
        {
            _taskOutputText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTaskOutput));
        }
    }
    public bool HasTaskOutput => !string.IsNullOrEmpty(_taskOutputText);

    /// <summary>
    /// Whether this task output has already been sent to Claude in the current context.
    /// Reset when context is lost (compaction, session restore).
    /// </summary>
    public bool IsTaskOutputSent
    {
        get => _isTaskOutputSent;
        set => SetProperty(ref _isTaskOutputSent, value);
    }

    /// <summary>
    /// Extracted completion summary text, displayed as a styled panel.
    /// </summary>
    public string? CompletionSummary
    {
        get => _completionSummary;
        set
        {
            SetProperty(ref _completionSummary, value);
            OnPropertyChanged(nameof(HasCompletionSummary));
        }
    }
    public bool HasCompletionSummary => !string.IsNullOrEmpty(_completionSummary);

    /// <summary>
    /// Extracts a completion summary from the end of the message.
    /// Looks for the last horizontal rule (---) separator followed by text
    /// that contains a completion marker. Splits the message: body stays in Text,
    /// summary goes to CompletionSummary.
    /// </summary>
    public void ExtractCompletionSummary()
    {
        if (Role != MessageRole.Assistant || string.IsNullOrEmpty(_text))
            return;

        // Find the last horizontal rule (3+ dashes on its own line)
        var lines = _text.Split('\n');
        var lastSepIndex = -1;

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].TrimEnd('\r').Trim();
            if (trimmed.Length >= 3 && trimmed.All(c => c == '-'))
            {
                lastSepIndex = i;
                break;
            }
        }

        if (lastSepIndex < 0 || lastSepIndex >= lines.Length - 1)
            return;

        // Extract text after the separator
        var summaryLines = lines.Skip(lastSepIndex + 1).ToArray();
        var summary = string.Join("\n", summaryLines).Trim();

        // Must be reasonable length
        if (summary.Length < 5 || summary.Length > 3000)
            return;

        // Must contain a completion marker
        var lowerSummary = summary.ToLowerInvariant();
        if (!CompletionMarkers.Any(m => lowerSummary.Contains(m)))
            return;

        // Split: set summary, trim main text
        CompletionSummary = summary;
        _text = string.Join("\n", lines.Take(lastSepIndex)).TrimEnd();
        OnPropertyChanged(nameof(Text));
    }

    public bool IsBookmarked
    {
        get => _isBookmarked;
        set => SetProperty(ref _isBookmarked, value);
    }

    public MessageViewModel(MessageRole role, string text = "")
    {
        Role = role;
        Text = text;
        Timestamp = DateTime.Now;

        ToolUses.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasToolUses));
            UpdateToolActivitySummary();
        };
    }

    private void UpdateToolActivitySummary()
    {
        if (ToolUses.Count == 0)
        {
            ToolActivitySummary = "";
            return;
        }

        var counts = new Dictionary<string, int>();
        foreach (var tool in ToolUses)
        {
            var name = tool.ToolName;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        var parts = counts.Select(kv => kv.Value > 1 ? $"{kv.Key} \u00d7{kv.Value}" : kv.Key);
        ToolActivitySummary = string.Join(", ", parts);
    }
}
