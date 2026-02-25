using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    private DispatcherTimer? _bgTaskTimer;

    public ObservableCollection<BackgroundTaskViewModel> BackgroundTasks { get; } = [];

    private bool _hasBackgroundTasks;
    public bool HasBackgroundTasks
    {
        get => _hasBackgroundTasks;
        private set => SetProperty(ref _hasBackgroundTasks, value);
    }

    private string _backgroundTasksHeaderText = "";
    public string BackgroundTasksHeaderText
    {
        get => _backgroundTasksHeaderText;
        private set => SetProperty(ref _backgroundTasksHeaderText, value);
    }

    private void InitializeBackgroundTaskTimer()
    {
        _bgTaskTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _bgTaskTimer.Tick += BgTaskTimer_Tick;
    }

    private void BgTaskTimer_Tick(object? sender, EventArgs e)
    {
        var hasRunning = false;
        foreach (var task in BackgroundTasks)
        {
            if (task.Status == BackgroundTaskStatus.Running)
            {
                task.UpdateElapsed();
                hasRunning = true;
            }
        }

        UpdateBackgroundTasksHeader();

        if (!hasRunning)
            _bgTaskTimer?.Stop();
    }

    private void TryTrackBackgroundTask(string toolName, string toolUseId, string input)
    {
        if (toolName != "Task" || string.IsNullOrEmpty(input) || !input.StartsWith('{'))
            return;

        // Skip if already tracking this tool use
        if (BackgroundTasks.Any(t => t.ToolUseId == toolUseId))
            return;

        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;

            // Only track if run_in_background is explicitly true
            if (!root.TryGetProperty("run_in_background", out var bgProp) || !bgProp.GetBoolean())
                return;

            var description = root.TryGetProperty("description", out var descProp)
                ? descProp.GetString() ?? "Background task"
                : "Background task";

            var info = new BackgroundTaskInfo(toolUseId, description, DateTime.UtcNow);
            var vm = new BackgroundTaskViewModel(info);
            BackgroundTasks.Add(vm);

            HasBackgroundTasks = true;
            UpdateBackgroundTasksHeader();

            // Start timer if not running
            if (_bgTaskTimer is { IsEnabled: false })
                _bgTaskTimer.Start();

            DiagnosticLogger.Log("BG_TASK", $"Started: {description} (toolUseId={toolUseId})");
        }
        catch
        {
            // Input JSON not yet complete or malformed — ignore
        }
    }

    private void TryUpdateBackgroundTask(string toolName, string toolUseId, string content)
    {
        if (toolName == "Task")
        {
            // Task tool result — extract agentId
            var bgTask = BackgroundTasks.FirstOrDefault(t => t.ToolUseId == toolUseId);
            if (bgTask is null) return;

            var agentIdMatch = Regex.Match(content, @"agentId:\s*(\S+)");
            if (agentIdMatch.Success)
            {
                bgTask.AgentId = agentIdMatch.Groups[1].Value;
                DiagnosticLogger.Log("BG_TASK", $"AgentId={bgTask.AgentId} for '{bgTask.Description}'");
            }
        }
        else if (toolName == "TaskOutput")
        {
            // TaskOutput result — find matching background task and update
            var taskId = FindTaskIdForToolResult(toolUseId);
            if (taskId is null) return;

            var bgTask = BackgroundTasks.FirstOrDefault(t => t.AgentId == taskId);
            if (bgTask is null) return;

            // If the result has meaningful content, the task is completed
            if (!string.IsNullOrWhiteSpace(content) && content.Length > 20)
            {
                bgTask.Complete(content);
                UpdateBackgroundTasksHeader();
                DiagnosticLogger.Log("BG_TASK", $"Completed: '{bgTask.Description}' ({content.Length} chars)");
            }
        }
        else if (toolName == "TaskStop")
        {
            var taskId = FindTaskIdForToolResult(toolUseId);
            if (taskId is null) return;

            var bgTask = BackgroundTasks.FirstOrDefault(t => t.AgentId == taskId);
            if (bgTask is null) return;

            bgTask.Fail();
            UpdateBackgroundTasksHeader();
            DiagnosticLogger.Log("BG_TASK", $"Stopped: '{bgTask.Description}'");
        }
    }

    /// <summary>
    /// Find the task_id from the ToolUseViewModel input JSON for TaskOutput/TaskStop tools.
    /// </summary>
    private string? FindTaskIdForToolResult(string toolUseId)
    {
        if (_currentAssistantMessage is null) return null;

        var toolVm = _currentAssistantMessage.ToolUses
            .FirstOrDefault(t => t.ToolUseId == toolUseId);

        if (toolVm?.Input is null || !toolVm.Input.StartsWith('{'))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(toolVm.Input);
            return doc.RootElement.TryGetProperty("task_id", out var tid)
                ? tid.GetString()
                : null;
        }
        catch { return null; }
    }

    private void ExecuteSendBgTaskOutput(object? param)
    {
        if (param is not BackgroundTaskViewModel bgTask || !bgTask.HasOutput || bgTask.IsSent)
            return;

        bgTask.IsSent = true;
        var prompt = $"Output from background task \"{bgTask.Description}\":\n\n" +
                     $"<background-task-output>\n{bgTask.OutputFull}\n</background-task-output>";
        _ = SendDirectAsync(prompt, null);
    }

    private void ExecuteDismissBgTask(object? param)
    {
        if (param is not BackgroundTaskViewModel bgTask) return;

        BackgroundTasks.Remove(bgTask);
        HasBackgroundTasks = BackgroundTasks.Count > 0;
        UpdateBackgroundTasksHeader();
    }

    private void UpdateBackgroundTasksHeader()
    {
        var total = BackgroundTasks.Count;
        var running = BackgroundTasks.Count(t => t.Status == BackgroundTaskStatus.Running);

        BackgroundTasksHeaderText = running > 0
            ? $"Background Tasks ({total} \u2014 {running} running)"
            : $"Background Tasks ({total})";
    }

    private void ClearBackgroundTasks()
    {
        BackgroundTasks.Clear();
        HasBackgroundTasks = false;
        _bgTaskTimer?.Stop();
    }
}
