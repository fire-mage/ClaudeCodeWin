using System.Reflection;
using System.Windows;
using System.Windows.Media;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class FeatureRequestWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly CcwApiService _api = new();

    public FeatureRequestWindow(AppSettings settings, SettingsService settingsService)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;

        if (!string.IsNullOrEmpty(_settings.UserEmail))
            EmailBox.Text = _settings.UserEmail;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        var description = DescriptionBox.Text.Trim();

        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            ShowStatus("Please enter a valid email address.", isError: true);
            return;
        }

        if (string.IsNullOrEmpty(description))
        {
            ShowStatus("Please describe the feature you'd like.", isError: true);
            return;
        }

        SendButton.IsEnabled = false;
        ShowStatus("Sending...", isError: false);

        var version = typeof(FeatureRequestWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "unknown";

        var (success, error) = await _api.SubmitFeatureRequestAsync(email, description, version);

        if (success)
        {
            // Remember email for next time
            _settings.UserEmail = email;
            _settingsService.Save(_settings);

            ShowStatus("Thank you! Your request has been submitted.", isError: false);
            DescriptionBox.Text = "";

            // Auto-close after short delay
            await Task.Delay(1500);
            Close();
        }
        else
        {
            ShowStatus(error ?? "Failed to send. Please try again.", isError: true);
            SendButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xf8, 0x51, 0x49))  // red
            : new SolidColorBrush(Color.FromRgb(0x3f, 0xb9, 0x50)); // green
    }
}
