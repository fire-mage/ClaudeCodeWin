using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public class MessageViewModel : ViewModelBase
{
    private string _text = string.Empty;
    private bool _isStreaming;
    private bool _isThinking;
    private string _toolActivitySummary = string.Empty;

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

    public bool HasToolUses => ToolUses.Count > 0;

    public string ToolActivitySummary
    {
        get => _toolActivitySummary;
        private set => SetProperty(ref _toolActivitySummary, value);
    }

    /// <summary>
    /// When non-null, this message shows a question with clickable option buttons.
    /// </summary>
    public QuestionDisplayModel? QuestionDisplay { get; set; }
    public bool HasQuestion => QuestionDisplay is not null;

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

public class ToolUseViewModel : ViewModelBase
{
    private string _output = string.Empty;
    private string _summary = string.Empty;
    private string _resultContent = string.Empty;
    private bool _isExpanded;
    private bool _isComplete;

    public string ToolName { get; }
    public string ToolUseId { get; }
    public string Input { get; private set; }

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public string Output
    {
        get => _output;
        set => SetProperty(ref _output, value);
    }

    public string ResultContent
    {
        get => _resultContent;
        set
        {
            SetProperty(ref _resultContent, value);
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(ResultPreview));
        }
    }

    public bool HasResult => !string.IsNullOrEmpty(_resultContent);

    /// <summary>
    /// First ~200 chars of result content for preview.
    /// </summary>
    public string ResultPreview =>
        _resultContent.Length > 200 ? _resultContent[..200] + "..." : _resultContent;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsComplete
    {
        get => _isComplete;
        set => SetProperty(ref _isComplete, value);
    }

    public RelayCommand ToggleExpandedCommand { get; }

    public ToolUseViewModel(string toolName, string toolUseId, string input)
    {
        ToolName = toolName;
        ToolUseId = toolUseId;
        Input = input;
        Summary = ParseToolSummary(toolName, input);
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>
    /// Update input with complete JSON (called when content_block_stop finalizes tool input).
    /// </summary>
    public void UpdateInput(string completeInput)
    {
        Input = completeInput;
        Summary = ParseToolSummary(ToolName, completeInput);
        IsComplete = true;
    }

    public static string ParseToolSummary(string toolName, string inputJson)
    {
        if (string.IsNullOrEmpty(inputJson) || inputJson == "{}")
            return "";

        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;

            return toolName switch
            {
                "Read" => ExtractString(root, "file_path", shortenPath: true),
                "Glob" => ExtractString(root, "pattern"),
                "Grep" => FormatGrep(root),
                "Bash" => FormatBash(root),
                "Edit" => ExtractString(root, "file_path", shortenPath: true),
                "Write" => ExtractString(root, "file_path", shortenPath: true),
                "NotebookEdit" => ExtractString(root, "notebook_path", shortenPath: true),
                "Task" => ExtractString(root, "description"),
                "WebSearch" => ExtractString(root, "query"),
                "WebFetch" => ExtractString(root, "url"),
                "AskUserQuestion" => FormatAskQuestion(root),
                _ => ""
            };
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string ExtractString(JsonElement root, string property, bool shortenPath = false)
    {
        if (!root.TryGetProperty(property, out var val) || val.ValueKind != JsonValueKind.String)
            return "";

        var s = val.GetString() ?? "";
        if (shortenPath)
            s = Path.GetFileName(s);
        return s;
    }

    private static string FormatGrep(JsonElement root)
    {
        var pattern = ExtractString(root, "pattern");
        var path = ExtractString(root, "path", shortenPath: true);
        if (!string.IsNullOrEmpty(path))
            return $"'{pattern}' in {path}";
        return $"'{pattern}'";
    }

    private static string FormatBash(JsonElement root)
    {
        var cmd = ExtractString(root, "command");
        return cmd.Length > 80 ? cmd[..80] + "..." : cmd;
    }

    private static string FormatAskQuestion(JsonElement root)
    {
        if (root.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qs.EnumerateArray())
            {
                if (q.TryGetProperty("question", out var qText))
                {
                    var text = qText.GetString() ?? "";
                    return text.Length > 60 ? text[..60] + "..." : text;
                }
            }
        }
        return "";
    }
}

/// <summary>
/// Model for displaying AskUserQuestion in the UI.
/// </summary>
public class QuestionDisplayModel
{
    public string QuestionText { get; set; } = "";
    public List<QuestionOption> Options { get; set; } = [];
}

public class QuestionOption
{
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
}
