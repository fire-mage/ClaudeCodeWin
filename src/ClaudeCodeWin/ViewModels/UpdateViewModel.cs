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
            // If update found, OnUpdateAvailable handler will show overlay
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
                UpdateService.ApplyUpdate(path);
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
}
