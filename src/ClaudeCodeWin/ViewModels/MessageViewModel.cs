using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

// Fix: DispatcherTimer was never cleaned up, causing memory leaks — added IDisposable
public class MessageViewModel : ViewModelBase, IDisposable
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
    // Fix: track last scanned text length to avoid O(n²) regex re-scanning during streaming
    private int _lastImageScanLength;
    // Fix WARNING #3: track previous text reference to detect full text replacement
    private string? _lastScannedText;
    // FIX (Issue 3): Instead of creating a new CTS per streaming delta (hundreds of allocations),
    // use a single CTS for cancellation on dispose and a volatile flag + batched candidates
    // with a debounce timer to coalesce rapid calls into a single File.Exists sweep.
    // FIX (WARNING #4): nullable field — Dispose() sets it to null via Interlocked.Exchange
    private System.Threading.CancellationTokenSource? _imageDetectionCts = new();
    private readonly List<string> _pendingImageCandidates = [];
    private System.Threading.Timer? _imageDetectionDebounce;
    // FIX: volatile ensures cross-thread visibility for dispose check in FlushImageCandidates
    private volatile bool _disposed;
    private bool _isStreaming;
    private bool _isThinking;
    private string _thinkingDurationText = ActivelyWorkingLabel;
    private DispatcherTimer? _thinkingTimer;
    private DateTime _thinkingStartTime;
    private string _thinkingText = string.Empty;
    private bool _isThinkingExpanded;
    private string _toolActivitySummary = string.Empty;
    private bool _isBookmarked;
    private string? _taskOutputText;
    private bool _isTaskOutputSent;
    private string? _completionSummary;
    private string? _reviewerLabel;

    public MessageRole Role { get; }
    public DateTime Timestamp { get; }

    /// <summary>
    /// When set, the message is displayed as a reviewer message with a special label/badge.
    /// </summary>
    public string? ReviewerLabel
    {
        get => _reviewerLabel;
        set
        {
            if (SetProperty(ref _reviewerLabel, value))
                OnPropertyChanged(nameof(IsReviewerMessage));
        }
    }

    public bool IsReviewerMessage => !string.IsNullOrEmpty(_reviewerLabel);

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
        if (_disposed || string.IsNullOrEmpty(text) || Role != MessageRole.Assistant) return;

        // Fix WARNING #3: Reset scan offset when text is replaced with different content
        // (not just appended to). Detects shrinking and full replacement of equal/greater length
        // by comparing a small window at the previous scan boundary (O(1) cost vs O(n) StartsWith).
        if (text.Length < _lastImageScanLength)
        {
            _lastImageScanLength = 0;
        }
        else if (_lastImageScanLength > 0 && !ReferenceEquals(text, _lastScannedText))
        {
            // Check 16 chars before the scan boundary — if they differ, text was replaced
            var checkFrom = Math.Max(0, _lastImageScanLength - 16);
            var checkLen = Math.Min(16, _lastImageScanLength - checkFrom);
            if (_lastScannedText is not null
                && checkFrom < _lastScannedText.Length
                && string.CompareOrdinal(text, checkFrom, _lastScannedText, checkFrom, checkLen) != 0)
            {
                _lastImageScanLength = 0;
            }
        }
        _lastScannedText = text;

        // Only scan new text region to avoid O(n²) regex on every streaming delta.
        var scanFrom = Math.Max(0, _lastImageScanLength - 260);
        _lastImageScanLength = text.Length;

        // Collect candidate paths (fast, no I/O) on UI thread
        var hasNew = false;
        lock (_pendingImageCandidates)
        {
            foreach (Match match in FilePathRegex.Matches(text, scanFrom))
            {
                var path = match.Value;
                if (!InlineImages.Contains(path) && !_pendingImageCandidates.Contains(path))
                {
                    _pendingImageCandidates.Add(path);
                    hasNew = true;
                }
            }
        }

        if (!hasNew) return;

        // FIX: Debounce — reset timer on each call. After 150ms of no new deltas,
        // fire a single background task to check all accumulated candidates.
        // FIX: protect timer dispose/recreate with same lock as _pendingImageCandidates
        // to prevent race between UI thread (here) and thread pool (FlushImageCandidates callback)
        lock (_pendingImageCandidates)
        {
            _imageDetectionDebounce?.Dispose();
            _imageDetectionDebounce = new System.Threading.Timer(_ => FlushImageCandidates(), null, 150, System.Threading.Timeout.Infinite);
        }
    }

    private void FlushImageCandidates()
    {
        // FIX (WARNING #2): After Interlocked.Exchange in Dispose(), _imageDetectionCts may be null.
        // Capture reference first; if null or disposed, bail out.
        var cts = _imageDetectionCts;
        if (cts is null) return;
        CancellationToken token;
        try { token = cts.Token; }
        catch (ObjectDisposedException) { return; }

        if (_disposed) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        // Snapshot and clear candidates (timer fires on thread pool)
        List<string> batch;
        lock (_pendingImageCandidates)
        {
            if (_pendingImageCandidates.Count == 0) return;
            batch = new List<string>(_pendingImageCandidates);
            _pendingImageCandidates.Clear();
        }
        foreach (var path in batch)
        {
            if (token.IsCancellationRequested) return;
            if (File.Exists(path))
            {
                dispatcher.BeginInvoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    if (!InlineImages.Contains(path))
                    {
                        InlineImages.Add(path);
                        OnPropertyChanged(nameof(HasInlineImages));
                    }
                });
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
        set
        {
            if (SetProperty(ref _isStreaming, value))
            {
                if (!value)
                    StopThinkingTimer();
            }
        }
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
                // Timer keeps running while IsStreaming is true
            }
        }
    }

    public string ThinkingDurationText
    {
        get => _thinkingDurationText;
        private set => SetProperty(ref _thinkingDurationText, value);
    }

    /// <summary>
    /// Accumulated text from Claude's extended thinking (thinking_delta events).
    /// </summary>
    public string ThinkingText
    {
        get => _thinkingText;
        set
        {
            if (SetProperty(ref _thinkingText, value))
                OnPropertyChanged(nameof(HasThinkingText));
        }
    }

    public bool HasThinkingText => !string.IsNullOrEmpty(_thinkingText);

    /// <summary>
    /// Whether the collapsed thinking section is expanded (post-completion).
    /// </summary>
    public bool IsThinkingExpanded
    {
        get => _isThinkingExpanded;
        set => SetProperty(ref _isThinkingExpanded, value);
    }

    private void StartThinkingTimer()
    {
        _thinkingStartTime = DateTime.UtcNow;
        ThinkingDurationText = ActivelyWorkingLabel;
        _thinkingTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _thinkingTimer.Tick -= ThinkingTimer_Tick; // prevent handler accumulation
        _thinkingTimer.Tick += ThinkingTimer_Tick;
        _thinkingTimer.Start();
    }

    /// <summary>
    /// Reset the thinking timer to 0 (called when new text/activity arrives on screen).
    /// </summary>
    public void ResetThinkingTimer()
    {
        _thinkingStartTime = DateTime.UtcNow;
        ThinkingDurationText = ActivelyWorkingLabel;
    }

    private void StopThinkingTimer()
    {
        if (_thinkingTimer is not null)
        {
            _thinkingTimer.Stop();
            _thinkingTimer.Tick -= ThinkingTimer_Tick;
        }
    }

    private const string ActivelyWorkingLabel = "Actively working";

    private void ThinkingTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _thinkingStartTime;
        if (elapsed.TotalSeconds <= 2)
        {
            ThinkingDurationText = ActivelyWorkingLabel;
            return;
        }
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
    private List<FileAttachment> _attachments = [];
    public List<FileAttachment> Attachments
    {
        get => _attachments;
        set
        {
            _attachments = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(ShowStandaloneAttachments));
            OnPropertyChanged(nameof(StandaloneAttachments));
        }
    }
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>
    /// Show standalone attachments block for non-image files, or all attachments when no ContentParts.
    /// When ContentParts exists, inline images are already shown there — only show non-image files here.
    /// </summary>
    public bool ShowStandaloneAttachments => HasContentParts
        ? Attachments.Any(a => !a.IsImage)
        : HasAttachments;

    /// <summary>Attachments to display in the standalone block (excludes images when ContentParts handles them).</summary>
    public IEnumerable<FileAttachment> StandaloneAttachments => HasContentParts
        ? Attachments.Where(a => !a.IsImage)
        : Attachments;

    /// <summary>
    /// Ordered content parts (text + images interleaved) for user messages with inline images.
    /// When set, the chat bubble renders these parts in order instead of text + attachments separately.
    /// </summary>
    private List<MessageContentPart>? _contentParts;
    public List<MessageContentPart>? ContentParts
    {
        get => _contentParts;
        set
        {
            _contentParts = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasContentParts));
            OnPropertyChanged(nameof(ShowStandaloneAttachments));
            OnPropertyChanged(nameof(StandaloneAttachments));
        }
    }
    public bool HasContentParts => ContentParts is { Count: > 0 };

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

    public void Dispose()
    {
        // FIX: Guard against double-dispose (ClearMessages + MainViewModel.Dispose both call this)
        if (_disposed) return;
        _disposed = true;

        GC.SuppressFinalize(this);

        StopThinkingTimer();
        _thinkingTimer = null;
        // Fix SUGGESTION #1: Dispose debounce timer under the same lock used in DetectImagePaths,
        // maintaining lock discipline in case either method is ever called off-thread.
        lock (_pendingImageCandidates)
        {
            _imageDetectionDebounce?.Dispose();
            _imageDetectionDebounce = null;
        }
        // FIX (WARNING #2): Use Interlocked.Exchange to atomically swap CTS to prevent race
        // where FlushImageCandidates captures .Token between Cancel() and Dispose() calls.
        var cts = Interlocked.Exchange(ref _imageDetectionCts, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        cts?.Dispose();
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
