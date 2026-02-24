using System.Windows;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.ViewModels;

public class UpdateViewModel : ViewModelBase
{
    private readonly UpdateService _updateService;
    private VersionInfo? _pendingUpdate;
    private string? _downloadedUpdatePath;

    private bool _isUpdating;
    private bool _showUpdateOverlay;
    private string _updateTitle = "";
    private string _updateStatusText = "";
    private int _updateProgressPercent;
    private bool _updateFailed;
    private bool _updateDownloading;
    private string _updateReleaseNotes = "";

    // CLI update state
    private CliUpdateService? _cliUpdateService;
    private CliVersionInfo? _pendingCliUpdate;
    private bool _showCliUpdateBadge;
    private bool _showCliUpdateOverlay;
    private bool _cliUpdating;
    private bool _cliUpdateFailed;
    private string _cliUpdateTitle = "";
    private string _cliUpdateStatusText = "";
    private string _cliUpdateLog = "";

    /// <summary>
    /// Raised when the update check wants to set status text on the main status bar.
    /// </summary>
    public event Action<string>? OnStatusTextChange;

    /// <summary>
    /// Raised when the user dismisses the update overlay (clicks "Later" or closes it after failure).
    /// </summary>
    public event Action? OnUpdateDismissed;

    public bool IsUpdating
    {
        get => _isUpdating;
        set => SetProperty(ref _isUpdating, value);
    }

    public bool ShowUpdateOverlay
    {
        get => _showUpdateOverlay;
        set => SetProperty(ref _showUpdateOverlay, value);
    }

    public string UpdateTitle
    {
        get => _updateTitle;
        set => SetProperty(ref _updateTitle, value);
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        set => SetProperty(ref _updateStatusText, value);
    }

    public int UpdateProgressPercent
    {
        get => _updateProgressPercent;
        set => SetProperty(ref _updateProgressPercent, value);
    }

    public bool UpdateFailed
    {
        get => _updateFailed;
        set => SetProperty(ref _updateFailed, value);
    }

    public bool UpdateDownloading
    {
        get => _updateDownloading;
        set => SetProperty(ref _updateDownloading, value);
    }

    public string UpdateReleaseNotes
    {
        get => _updateReleaseNotes;
        set => SetProperty(ref _updateReleaseNotes, value);
    }

    // CLI update properties
    public bool ShowCliUpdateBadge
    {
        get => _showCliUpdateBadge;
        set => SetProperty(ref _showCliUpdateBadge, value);
    }

    public bool ShowCliUpdateOverlay
    {
        get => _showCliUpdateOverlay;
        set => SetProperty(ref _showCliUpdateOverlay, value);
    }

    public bool CliUpdating
    {
        get => _cliUpdating;
        set => SetProperty(ref _cliUpdating, value);
    }

    public bool CliUpdateFailed
    {
        get => _cliUpdateFailed;
        set => SetProperty(ref _cliUpdateFailed, value);
    }

    public string CliUpdateTitle
    {
        get => _cliUpdateTitle;
        set => SetProperty(ref _cliUpdateTitle, value);
    }

    public string CliUpdateStatusText
    {
        get => _cliUpdateStatusText;
        set => SetProperty(ref _cliUpdateStatusText, value);
    }

    public string CliUpdateLog
    {
        get => _cliUpdateLog;
        set => SetProperty(ref _cliUpdateLog, value);
    }

    public AsyncRelayCommand CheckCliUpdatesCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public UpdateViewModel(UpdateService updateService, AppSettings settings)
    {
        _updateService = updateService;

        CheckForUpdatesCommand = new AsyncRelayCommand(async () =>
        {
            OnStatusTextChange?.Invoke("Checking for updates...");
            var update = await _updateService.CheckForUpdateAsync();
            if (update is null)
            {
                OnStatusTextChange?.Invoke("");
                MessageBox.Show($"You are on the latest version ({_updateService.CurrentVersion}).",
                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });

        CheckCliUpdatesCommand = new AsyncRelayCommand(async () =>
        {
            if (_cliUpdateService is null) return;
            OnStatusTextChange?.Invoke("Checking for CLI updates...");
            var update = await _cliUpdateService.CheckForUpdateAsync();
            OnStatusTextChange?.Invoke("");
            if (update is null)
            {
                MessageBox.Show(
                    $"Claude Code CLI is up to date ({_cliUpdateService.CurrentCliVersion ?? "unknown"}).",
                    "Check for CLI Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // User explicitly requested check — show the update overlay immediately
                ShowCliUpdateOverlay = true;
            }
        });

        // Subscribe to update events
        _updateService.OnUpdateAvailable += info =>
        {
            RunOnUI(() =>
            {
                _pendingUpdate = info;
                UpdateTitle = $"v{_updateService.CurrentVersion}  →  v{info.Version}";
                UpdateReleaseNotes = info.ReleaseNotes ?? "";
                UpdateStatusText = "A new version is available.";
                UpdateProgressPercent = 0;
                UpdateFailed = false;
                UpdateDownloading = false;
                ShowUpdateOverlay = true;
            });
        };

        _updateService.OnDownloadProgress += percent =>
        {
            RunOnUI(() =>
            {
                UpdateProgressPercent = percent;
                UpdateStatusText = $"Downloading update... {percent}%";
            });
        };

        _updateService.OnUpdateReady += path =>
        {
            RunOnUI(() =>
            {
                _downloadedUpdatePath = path;
                UpdateStatusText = "Update ready — restarting...";
                UpdateProgressPercent = 100;
                UpdateService.ApplyUpdate(path, _pendingUpdate?.Version);
            });
        };

        _updateService.OnError += error =>
        {
            RunOnUI(() =>
            {
                IsUpdating = false;
                UpdateFailed = true;
                UpdateDownloading = false;
                UpdateStatusText = error;
            });
        };

        _updateService.UpdateChannel = settings.UpdateChannel ?? "stable";
    }

    /// <summary>
    /// Run a one-time update check (used at startup before showing WelcomeDialog).
    /// Returns true if an update was found and the overlay is now visible.
    /// </summary>
    public async Task<bool> CheckOnStartupAsync()
    {
        var result = await _updateService.CheckForUpdateAsync();
        return result is not null;
    }

    /// <summary>
    /// Start the background periodic update check timer (every 4 hours).
    /// Call this after the initial startup check is done.
    /// </summary>
    public void StartPeriodicCheck()
    {
        _updateService.StartPeriodicCheck();
    }

    public void SetUpdateChannel(string channel)
    {
        _updateService.UpdateChannel = channel;
    }

    public void StartUpdate()
    {
        if (_pendingUpdate is null) return;
        IsUpdating = true;
        UpdateDownloading = true;
        UpdateStatusText = "Starting download...";
        _ = _updateService.DownloadAndApplyAsync(_pendingUpdate);
    }

    public void DismissUpdate()
    {
        ShowUpdateOverlay = false;
        UpdateFailed = false;
        UpdateDownloading = false;
        IsUpdating = false;
        OnUpdateDismissed?.Invoke();
    }

    // --- CLI update methods ---

    public void InitCliUpdate(CliUpdateService svc)
    {
        _cliUpdateService = svc;

        svc.OnCliUpdateAvailable += info =>
        {
            RunOnUI(() =>
            {
                _pendingCliUpdate = info;
                CliUpdateTitle = $"v{info.CurrentVersion}  →  v{info.LatestVersion}";
                CliUpdateStatusText = "A new CLI version is available.";
                CliUpdateFailed = false;
                CliUpdating = false;
                CliUpdateLog = "";
                ShowCliUpdateBadge = true;
                OnStatusTextChange?.Invoke($"CLI update available: v{info.LatestVersion}");
            });
        };

        svc.OnCliUpdateProgress += text =>
        {
            RunOnUI(() =>
            {
                CliUpdateStatusText = text;
                if (_cliUpdateLog.Length > 0)
                    CliUpdateLog += "\n";
                CliUpdateLog += text;
            });
        };

        svc.OnCliUpdateCompleted += newVersion =>
        {
            RunOnUI(() =>
            {
                ShowCliUpdateOverlay = false;
                ShowCliUpdateBadge = false;
                CliUpdating = false;
                _pendingCliUpdate = null;
                OnStatusTextChange?.Invoke($"CLI updated to v{newVersion}");
            });
        };

        svc.OnCliUpdateFailed += error =>
        {
            RunOnUI(() =>
            {
                CliUpdating = false;
                CliUpdateFailed = true;
                CliUpdateStatusText = error;
            });
        };
    }

    public void StartCliPeriodicCheck()
    {
        _cliUpdateService?.StartPeriodicCheck();
    }

    public void ShowCliUpdatePrompt()
    {
        if (_pendingCliUpdate is null) return;
        ShowCliUpdateOverlay = true;
    }

    public void StartCliUpdate()
    {
        if (_pendingCliUpdate is null || _cliUpdateService is null) return;
        CliUpdating = true;
        CliUpdateFailed = false;
        CliUpdateLog = "";
        CliUpdateStatusText = "Starting update...";
        _ = _cliUpdateService.UpdateCliAsync(_pendingCliUpdate.LatestVersion);
    }

    public void DismissCliUpdate()
    {
        ShowCliUpdateOverlay = false;
        CliUpdateFailed = false;
        CliUpdating = false;
    }
}
