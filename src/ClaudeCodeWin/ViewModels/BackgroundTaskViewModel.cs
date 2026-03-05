using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.ViewModels;

public class BackgroundTaskViewModel : ViewModelBase
{
    private readonly BackgroundTaskInfo _info;
    private string _elapsedText = "0s";
    private bool _isSent;

    public BackgroundTaskViewModel(BackgroundTaskInfo info)
    {
        _info = info;
    }

    public string ToolUseId => _info.ToolUseId;
    public string Description => _info.Description;
    public DateTime StartTime => _info.StartTime;

    public string? AgentId
    {
        get => _info.AgentId;
        set => _info.AgentId = value;
    }

    public BackgroundTaskStatus Status
    {
        get => _info.Status;
        private set
        {
            if (_info.Status == value) return;
            _info.Status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusIcon));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(IsRunning));
        }
    }

    public string StatusIcon => Status switch
    {
        BackgroundTaskStatus.Running => "\u23F3",   // ⏳
        BackgroundTaskStatus.Completed => "\u2705", // ✅
        BackgroundTaskStatus.Failed => "\u274C",    // ❌
        _ => "\u2753"
    };

    public bool IsRunning => Status == BackgroundTaskStatus.Running;
    public bool IsCompleted => Status != BackgroundTaskStatus.Running;

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public string? OutputPreview => _info.Output is { Length: > 200 }
        ? _info.Output[..200] + "..."
        : _info.Output;

    public string? OutputFull => _info.Output;
    public bool HasOutput => _info.Output is not null;

    public bool IsSent
    {
        get => _isSent;
        set => SetProperty(ref _isSent, value);
    }

    public void UpdateElapsed()
    {
        // Fix: don't overwrite "Done in..." / "Failed after..." text once task is finished
        if (IsCompleted) return;
        var elapsed = DateTime.UtcNow - _info.StartTime;
        ElapsedText = FormatElapsed(elapsed);
    }

    public void Complete(string? output)
    {
        _info.EndTime = DateTime.UtcNow;
        _info.Output = output;
        Status = BackgroundTaskStatus.Completed;

        var duration = _info.EndTime.Value - _info.StartTime;
        ElapsedText = $"Done in {FormatElapsed(duration)}";

        OnPropertyChanged(nameof(OutputPreview));
        OnPropertyChanged(nameof(OutputFull));
        OnPropertyChanged(nameof(HasOutput));
    }

    public void Fail()
    {
        _info.EndTime = DateTime.UtcNow;
        Status = BackgroundTaskStatus.Failed;

        var duration = _info.EndTime.Value - _info.StartTime;
        ElapsedText = $"Failed after {FormatElapsed(duration)}";
    }

    private static string FormatElapsed(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{(int)ts.TotalSeconds}s";
    }
}
