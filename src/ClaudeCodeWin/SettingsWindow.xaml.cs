using System.Threading;
using System.Windows;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly MainViewModel _viewModel;
    private readonly UpdateViewModel _updateViewModel;
    private readonly string? _workingDir;
    private readonly InstructionsService _instructions = new();
    private readonly SpeechRecognitionService? _speechService;
    private readonly VectorMemoryService? _vectorMemory;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _vectorDownloadCts;
    private bool _initialized;

    public SettingsWindow(AppSettings settings, SettingsService settingsService, MainViewModel viewModel,
        UpdateViewModel updateViewModel, string? workingDir,
        SpeechRecognitionService? speechService = null, VectorMemoryService? vectorMemory = null)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;
        _viewModel = viewModel;
        _updateViewModel = updateViewModel;
        _workingDir = workingDir;
        _speechService = speechService;
        _vectorMemory = vectorMemory;

        // Set current state
        if (settings.UpdateChannel == "beta")
            BetaRadio.IsChecked = true;
        else
            StableRadio.IsChecked = true;

        UpdateInstructionsSummary();
        UpdateServersSummary();
        UpdateActivationSummary();
        UpdateApiKeysSummary();
        InitVoiceInput();
        InitVectorMemory();

        _initialized = true;
    }

    private void UpdateChannel_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;

        var channel = BetaRadio.IsChecked == true ? "beta" : "stable";
        _settings.UpdateChannel = channel;
        _settingsService.Save(_settings);
        _updateViewModel.SetUpdateChannel(channel);
    }

    private void ManageInstructions_Click(object sender, RoutedEventArgs e)
    {
        var systemInstruction = MainViewModel.GetSystemInstructionText();
        var dlg = new InstructionsWindow(_instructions, _workingDir, systemInstruction) { Owner = this };
        dlg.ShowDialog();
        UpdateInstructionsSummary();
    }

    private void UpdateInstructionsSummary()
    {
        InstructionsSummary.Text = InstructionsWindow.BuildSummary(_instructions, _workingDir);
    }

    private void ManageServers_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ServerRegistryWindow(_settings, _settingsService) { Owner = this };
        dlg.ShowDialog();
        UpdateServersSummary();
    }

    private void UpdateServersSummary()
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(_settings.SshKeyPath))
            parts.Add($"SSH key: {System.IO.Path.GetFileName(_settings.SshKeyPath)}");
        else
            parts.Add("No SSH key configured");

        var count = _settings.Servers.Count;
        parts.Add(count switch
        {
            0 => "No servers configured",
            1 => "1 server",
            _ => $"{count} servers"
        });

        ServersSummary.Text = string.Join("  |  ", parts);
    }

    private void ManageActivationCode_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ActivationCodeWindow(_settings, _settingsService) { Owner = this };
        dlg.ShowDialog();
        UpdateActivationSummary();
    }

    private void UpdateActivationSummary()
    {
        if (!string.IsNullOrEmpty(_settings.ActivationCode) && _settings.ActivatedFeatures.Count > 0)
            ActivationSummary.Text = $"Active code: {_settings.ActivationCode}  |  Features: {string.Join(", ", _settings.ActivatedFeatures)}";
        else
            ActivationSummary.Text = "No activation code applied";
    }

    private void ManageApiKeys_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ApiKeyDialog(_settings, _settingsService) { Owner = this };
        dlg.ShowDialog();
        UpdateApiKeysSummary();
    }

    private void UpdateApiKeysSummary()
    {
        if (_settings.ApiKeys.Count == 0)
        {
            ApiKeysSummary.Text = "No API keys configured";
            return;
        }

        var parts = new List<string>();
        foreach (var key in _settings.ApiKeys)
        {
            var (days, isExpired, isWarning) = key.GetExpiryStatus();
            if (!key.ExpiresAt.HasValue)
                parts.Add($"{key.ServiceName}: no expiry");
            else if (isExpired)
                parts.Add($"{key.ServiceName}: expired");
            else
                parts.Add($"{key.ServiceName}: {days}d{(isWarning ? " left" : "")}");
        }

        ApiKeysSummary.Text = string.Join("  |  ", parts);
    }

    // ── Voice Input ──

    private void InitVoiceInput()
    {
        // Populate model combo
        foreach (var model in WhisperModelManager.AvailableModels)
            VoiceModelCombo.Items.Add(WhisperModelManager.GetModelDisplayName(model));

        var selectedIdx = Array.IndexOf(WhisperModelManager.AvailableModels, _settings.VoiceInputModel);
        if (selectedIdx < 0) selectedIdx = 2; // default to "small"
        VoiceModelCombo.SelectedIndex = selectedIdx;

        VoiceEnabledCheck.IsChecked = _settings.VoiceInputEnabled;
        UpdateVoiceUI();
    }

    private void UpdateVoiceUI()
    {
        var enabled = VoiceEnabledCheck.IsChecked == true;
        VoiceModelPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled)
        {
            VoiceSummary.Text = "Disabled — no microphone button shown";
            return;
        }

        var model = GetSelectedModel();
        var downloaded = WhisperModelManager.IsModelDownloaded(model);

        VoiceDownloadBtn.Visibility = downloaded ? Visibility.Collapsed : Visibility.Visible;
        VoiceDeleteBtn.Visibility = downloaded ? Visibility.Visible : Visibility.Collapsed;

        if (!SpeechRecognitionService.HasMicrophone())
        {
            VoiceSummary.Text = "No microphone detected";
            return;
        }

        var modelDisplay = WhisperModelManager.GetModelDisplayName(model);
        VoiceSummary.Text = downloaded
            ? $"Ready — model: {modelDisplay}"
            : $"Model not downloaded — model: {modelDisplay}";
    }

    private string GetSelectedModel()
    {
        var models = WhisperModelManager.AvailableModels;
        var idx = VoiceModelCombo.SelectedIndex;
        return idx >= 0 && idx < models.Length ? models[idx] : "small";
    }

    private void VoiceEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.VoiceInputEnabled = VoiceEnabledCheck.IsChecked == true;
        _settingsService.Save(_settings);
        UpdateVoiceUI();
    }

    private void VoiceModel_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _settings.VoiceInputModel = GetSelectedModel();
        _settingsService.Save(_settings);
        UpdateVoiceUI();
    }

    private async void VoiceDownload_Click(object sender, RoutedEventArgs e)
    {
        var model = GetSelectedModel();
        VoiceDownloadBtn.IsEnabled = false;
        VoiceDownloadProgress.Visibility = Visibility.Visible;
        VoiceDownloadProgress.Value = 0;
        VoiceDownloadStatus.Text = "Downloading...";

        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();

        try
        {
            var success = await WhisperModelManager.DownloadModelAsync(
                model,
                (downloaded, total) =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        var pct = total > 0 ? (double)downloaded / total * 100 : 0;
                        VoiceDownloadProgress.Value = pct;
                        VoiceDownloadStatus.Text = $"Downloading... {downloaded / 1_048_576} / {total / 1_048_576} MB ({pct:F0}%)";
                    });
                },
                _downloadCts.Token);

            if (success)
            {
                VoiceDownloadStatus.Text = "Download complete!";

                // Auto-load model into speech service
                if (_speechService is not null)
                {
                    VoiceDownloadStatus.Text = "Loading model...";
                    await _speechService.LoadModelAsync(model);
                    VoiceDownloadStatus.Text = "Model loaded and ready!";
                }
            }
            else
            {
                VoiceDownloadStatus.Text = "Download cancelled.";
            }
        }
        catch (Exception ex)
        {
            VoiceDownloadStatus.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            VoiceDownloadBtn.IsEnabled = true;
            VoiceDownloadProgress.Visibility = Visibility.Collapsed;
            UpdateVoiceUI();
        }
    }

    private void VoiceDelete_Click(object sender, RoutedEventArgs e)
    {
        var model = GetSelectedModel();
        var result = MessageBox.Show(
            $"Delete the {model} model file?\nYou can re-download it later.",
            "Delete Model", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        WhisperModelManager.DeleteModel(model);
        VoiceDownloadStatus.Text = "Model deleted.";
        UpdateVoiceUI();
    }

    // ── Vector Memory ──

    private void InitVectorMemory()
    {
        VectorEnabledCheck.IsChecked = _settings.VectorMemoryEnabled;
        UpdateVectorUI();
    }

    private void UpdateVectorUI()
    {
        var enabled = VectorEnabledCheck.IsChecked == true;
        VectorModelPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled)
        {
            VectorSummary.Text = "Disabled — no semantic context injection";
            return;
        }

        var downloaded = EmbeddingModelManager.IsModelDownloaded();
        VectorDownloadPanel.Visibility = downloaded ? Visibility.Collapsed : Visibility.Visible;
        VectorDeleteBtn.Visibility = downloaded ? Visibility.Visible : Visibility.Collapsed;

        if (downloaded)
        {
            var isLoaded = _vectorMemory?.IsAvailable == true;
            VectorModelStatus.Text = isLoaded
                ? "Model: all-MiniLM-L6-v2 — loaded and ready"
                : "Model: all-MiniLM-L6-v2 — downloaded (restart to load)";
            VectorSummary.Text = isLoaded ? "Active — semantic search enabled" : "Model downloaded — restart to activate";

            // Show index stats
            if (_vectorMemory?.IsAvailable == true && !string.IsNullOrEmpty(_workingDir))
            {
                var (totalDocs, totalChunks, kbCount, chatCount, noteCount) = _vectorMemory.GetIndexStats(_workingDir);
                VectorIndexStats.Text = totalChunks > 0
                    ? $"Indexed: {totalDocs} documents, {totalChunks} chunks (KB: {kbCount}, Chats: {chatCount}, Notes: {noteCount})"
                    : "No documents indexed yet for this project";
            }
            else
            {
                VectorIndexStats.Text = "";
            }
        }
        else
        {
            VectorModelStatus.Text = "Model not downloaded";
            VectorSummary.Text = "Download the embedding model to enable semantic search";
            VectorIndexStats.Text = "";
        }
    }

    private void VectorEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.VectorMemoryEnabled = VectorEnabledCheck.IsChecked == true;
        _settingsService.Save(_settings);
        UpdateVectorUI();
    }

    private async void VectorDownload_Click(object sender, RoutedEventArgs e)
    {
        VectorDownloadBtn.IsEnabled = false;
        VectorDownloadProgress.Visibility = Visibility.Visible;
        VectorDownloadProgress.Value = 0;
        VectorDownloadStatus.Text = "Downloading...";

        _vectorDownloadCts?.Cancel();
        _vectorDownloadCts?.Dispose();
        _vectorDownloadCts = new CancellationTokenSource();

        try
        {
            var success = await EmbeddingModelManager.DownloadAsync(
                (downloaded, total) =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        var pct = total > 0 ? (double)downloaded / total * 100 : 0;
                        VectorDownloadProgress.Value = pct;
                        VectorDownloadStatus.Text = $"Downloading... {downloaded / 1_048_576} / {total / 1_048_576} MB ({pct:F0}%)";
                    });
                },
                _vectorDownloadCts.Token);

            if (success)
            {
                VectorDownloadStatus.Text = "Download complete! Restart the app to activate.";

                // Try to initialize immediately
                if (_vectorMemory is not null && !_vectorMemory.IsAvailable)
                {
                    VectorDownloadStatus.Text = "Loading model...";
                    await Task.Run(() => _vectorMemory.InitializeAsync());
                    VectorDownloadStatus.Text = _vectorMemory.IsAvailable
                        ? "Model loaded and ready!"
                        : "Download complete! Restart the app to activate.";
                }
            }
            else
            {
                VectorDownloadStatus.Text = "Download cancelled.";
            }
        }
        catch (Exception ex)
        {
            VectorDownloadStatus.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            VectorDownloadBtn.IsEnabled = true;
            VectorDownloadProgress.Visibility = Visibility.Collapsed;
            UpdateVectorUI();
        }
    }

    private void VectorDelete_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Delete the embedding model?\nYou can re-download it later.",
            "Delete Model", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        // Shutdown ONNX session first to release file handles
        _vectorMemory?.Shutdown();
        EmbeddingModelManager.DeleteModel();
        VectorDownloadStatus.Text = EmbeddingModelManager.IsModelDownloaded()
            ? "Could not delete model — file may be in use. Restart and try again."
            : "Model deleted.";
        UpdateVectorUI();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _vectorDownloadCts?.Cancel();
        _vectorDownloadCts?.Dispose();
        Close();
    }
}
