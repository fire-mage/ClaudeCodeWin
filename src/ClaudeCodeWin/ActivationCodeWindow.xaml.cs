using System.Windows;
using System.Windows.Media;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class ActivationCodeWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly CcwApiService _api = new();

    public ActivationCodeWindow(AppSettings settings, SettingsService settingsService)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;

        UpdateCurrentStatus();
    }

    private void UpdateCurrentStatus()
    {
        if (!string.IsNullOrEmpty(_settings.ActivationCode) && _settings.ActivatedFeatures.Count > 0)
        {
            CurrentStatusPanel.Visibility = Visibility.Visible;
            CurrentCodeText.Text = $"Active code: {_settings.ActivationCode}";
            CurrentFeaturesText.Text = $"Features: {string.Join(", ", _settings.ActivatedFeatures)}";
        }
        else
        {
            CurrentStatusPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (string.IsNullOrEmpty(code) || code.Length < 8)
        {
            ShowStatus("Please enter a valid 8-character code.", isError: true);
            return;
        }

        ActivateButton.IsEnabled = false;
        ShowStatus("Validating...", isError: false);

        // FIX: wrap network call in try-catch - unhandled exception in async void crashes app
        try
        {
            var (result, error) = await _api.ActivateCodeAsync(code);

            // FIX: guard UI access after await - window may have closed during network call
            if (!IsLoaded) return;

            if (result != null)
            {
                _settings.ActivationCode = code.ToUpperInvariant();
                _settings.ActivatedFeatures = result.Features;
                _settingsService.Save(_settings);

                ShowStatus($"Activated! Features: {string.Join(", ", result.Features)}", isError: false);
                UpdateCurrentStatus();
                CodeBox.Text = "";
            }
            else
            {
                ShowStatus(error ?? "Invalid code. Please try again.", isError: true);
            }
        }
        catch (Exception ex)
        {
            // FIX: guard UI access - window may have closed during network call
            if (IsLoaded)
                ShowStatus($"Unexpected error: {ex.Message}", isError: true);
        }
        // FIX: moved to finally to ensure button re-enables even if catch throws
        finally
        {
            if (IsLoaded)
                ActivateButton.IsEnabled = true;
        }
    }

    private void Deactivate_Click(object sender, RoutedEventArgs e)
    {
        _settings.ActivationCode = null;
        _settings.ActivatedFeatures = [];
        _settingsService.Save(_settings);

        UpdateCurrentStatus();
        ShowStatus("Code deactivated.", isError: false);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xf8, 0x51, 0x49))
            : new SolidColorBrush(Color.FromRgb(0x3f, 0xb9, 0x50));
    }
}
