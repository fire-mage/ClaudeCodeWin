using System.IO;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

/// <summary>
/// Controls partial — per-chat control request handling is now in ChatSessionViewModel.
/// This file retains only project-level conflict detection and resolution.
/// </summary>
public partial class MainViewModel
{
    private const int ConflictPauseTimeoutSeconds = 30;

    private bool IsFileConflictWithTeam(string filePath)
    {
        var orch = _orchestratorService;
        if (orch == null) return false;
        var state = orch.State;
        if (state is not (OrchestratorState.Running or OrchestratorState.WaitingForWork))
            return false;

        string normalizedPath;
        try { normalizedPath = Path.GetFullPath(filePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            DiagnosticLogger.Log("CONFLICT_CHECK_ERROR", $"Bad input path: {ex.Message}");
            return false;
        }

        var activeFiles = orch.GetActiveSessionChangedFiles();
        if (IsPathInList(activeFiles, normalizedPath))
            return true;

        if (state == OrchestratorState.Running && !string.IsNullOrEmpty(WorkingDirectory))
        {
            var inProgressFeatures = _backlogService.GetFeaturesByStatus(WorkingDirectory, FeatureStatus.InProgress);
            foreach (var feature in inProgressFeatures)
            {
                foreach (var phase in feature.Phases)
                {
                    if (IsPathInList(phase.ChangedFiles, normalizedPath))
                        return true;
                }
            }
        }

        return false;
    }

    private static bool IsPathInList(IEnumerable<string> paths, string normalizedPath)
    {
        foreach (var f in paths)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(f), normalizedPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                DiagnosticLogger.Log("CONFLICT_CHECK_SKIP", $"Skipping malformed path '{f}': {ex.Message}");
            }
        }
        return false;
    }

    private async Task HandleConflictPauseAsync(string requestId, string toolUseId, string filePath,
        ClaudeCliService? callerCli = null)
    {
        // Use the CLI that triggered the conflict, falling back to active session
        var cli = callerCli ?? ActiveChatSession?.CliService;

        if (_conflictPauseCts != null)
        {
            cli?.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            return;
        }

        _teamPausedForConflict = true;
        _conflictEditAnyway = false;
        _pendingConflictRequestId = requestId;
        _pendingConflictToolUseId = toolUseId;
        ConflictBannerText = $"Team is editing {Path.GetFileName(filePath)} \u2014 pausing team...";
        OnPropertyChanged(nameof(IsConflictBannerVisible));
        OnPropertyChanged(nameof(IsConflictActionable));

        _conflictPauseCts = new CancellationTokenSource(TimeSpan.FromSeconds(ConflictPauseTimeoutSeconds));
        var orch = _orchestratorService;
        if (orch == null || cli == null)
        {
            ClearConflictPauseState();
            cli?.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            return;
        }

        var responseSent = false;
        try
        {
            await orch.SoftPauseAsync(_conflictPauseCts.Token);
            ConflictBannerText = "Team paused \u2014 working...";
            cli.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            responseSent = true;
        }
        catch (OperationCanceledException)
        {
            var pendingReqId = _pendingConflictRequestId;
            if (_conflictEditAnyway)
            {
                if (!responseSent && cli.IsProcessRunning)
                    cli.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
                responseSent = true;
                ClearConflictPauseState();
            }
            else
            {
                if (_teamPausedForConflict)
                    ClearConflictPauseState();
                if (!responseSent && cli.IsProcessRunning && pendingReqId != null)
                    cli.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
            }
        }
        catch (Exception ex)
        {
            var pendingReqId = _pendingConflictRequestId;
            DiagnosticLogger.Log("CONFLICT_PAUSE_ERROR", ex.Message);
            if (_teamPausedForConflict)
                ClearConflictPauseState();
            if (!responseSent && cli.IsProcessRunning && pendingReqId != null)
                cli.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
        }
        finally
        {
            if (_conflictPauseCts != null)
            {
                _conflictPauseCts.Dispose();
                _conflictPauseCts = null;
            }
            _pendingConflictRequestId = null;
            _pendingConflictToolUseId = null;
            OnPropertyChanged(nameof(IsConflictActionable));

            if (_teamPausedForConflict && !string.IsNullOrEmpty(ConflictBannerText))
            {
                _conflictBannerClearTimer?.Stop();
                _conflictBannerClearTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(2)
                };
                _conflictBannerClearTimer.Tick += (_, _) =>
                {
                    _conflictBannerClearTimer?.Stop();
                    _conflictBannerClearTimer = null;
                    if (_teamPausedForConflict)
                    {
                        DiagnosticLogger.Log("CONFLICT_BANNER_TIMEOUT",
                            "Conflict banner auto-cleared after 2 min safety timeout.");
                        ClearConflictPauseState();
                    }
                };
                _conflictBannerClearTimer.Start();
            }
        }
    }

    private void ClearConflictPauseState()
    {
        ConflictBannerText = "";
        _teamPausedForConflict = false;
        OnPropertyChanged(nameof(IsConflictBannerVisible));
        _orchestratorService?.ClearPendingSoftPause();
        _orchestratorService?.ResumeIfSoftPaused();
    }

    private void ResumeTeamAfterConflict()
    {
        if (!_teamPausedForConflict) return;
        _conflictPauseCts?.Cancel();
        ClearConflictPauseState();
    }

    private void HandleEditAnyway()
    {
        _conflictEditAnyway = true;
        _conflictPauseCts?.Cancel();
    }

    private void HandleCancelConflict()
    {
        var reqId = _pendingConflictRequestId;
        var toolId = _pendingConflictToolUseId;
        _pendingConflictRequestId = null;
        _pendingConflictToolUseId = null;
        _conflictPauseCts?.Cancel();

        var cli = ActiveChatSession?.CliService;
        if (reqId != null && cli?.IsProcessRunning == true)
            cli.SendControlResponse(reqId, "deny", toolUseId: toolId,
                errorMessage: "User cancelled due to file conflict");

        ActiveChatSession?.CancelProcessing();
    }
}
