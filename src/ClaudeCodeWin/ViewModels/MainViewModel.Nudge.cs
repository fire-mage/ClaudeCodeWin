using System.Windows.Threading;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public partial class MainViewModel
{
    private const int NudgeInactivitySeconds = 300; // 5 minutes
    private const int MaxNudgesPerTurn = 3;
    private const string NudgeMessageText =
        "It seems like you might be stuck. Please check your current state and continue, " +
        "or let me know what's blocking you.";

    private DispatcherTimer? _nudgeTimer;
    private DateTime _lastActivityTime;
    private int _nudgeCount;
    private bool _showNudgeButton;

    public bool ShowNudgeButton
    {
        get => _showNudgeButton;
        set => SetProperty(ref _showNudgeButton, value);
    }

    private void InitializeNudge()
    {
        _nudgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _nudgeTimer.Tick += NudgeTimer_Tick;
    }

    private void ResetNudgeActivity()
    {
        _lastActivityTime = DateTime.UtcNow;
        if (_showNudgeButton)
            ShowNudgeButton = false;

        // Reset thinking duration timer to show time since last screen activity
        _messageAssembler.CurrentMessage?.ResetThinkingTimer();
    }

    private void StartNudgeTimer()
    {
        _nudgeCount = 0;
        _lastActivityTime = DateTime.UtcNow;
        ShowNudgeButton = false;
        _nudgeTimer?.Start();
    }

    private void StopNudgeTimer()
    {
        _nudgeTimer?.Stop();
        ShowNudgeButton = false;
    }

    private void NudgeTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsProcessing)
        {
            StopNudgeTimer();
            return;
        }

        if (_nudgeCount >= MaxNudgesPerTurn)
            return;

        var elapsed = (DateTime.UtcNow - _lastActivityTime).TotalSeconds;
        if (elapsed >= NudgeInactivitySeconds && !_showNudgeButton)
            ShowNudgeButton = true;
    }

    private void ExecuteNudge()
    {
        if (!IsProcessing || _nudgeCount >= MaxNudgesPerTurn)
            return;

        _nudgeCount++;
        ShowNudgeButton = false;
        _lastActivityTime = DateTime.UtcNow;

        // Send nudge immediately via CLI stdin (don't queue — CLI is already running)
        Messages.Add(new MessageViewModel(MessageRole.User, NudgeMessageText));
        _cliService.SendMessage(NudgeMessageText);

        DiagnosticLogger.Log("NUDGE", $"Nudge #{_nudgeCount} sent immediately");
    }
}
