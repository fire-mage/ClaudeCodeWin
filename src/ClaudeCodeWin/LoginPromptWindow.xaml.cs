using System.Windows;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class LoginPromptWindow : Window
{
    private readonly ClaudeCodeDependencyService _dependencyService;
    private readonly string _claudeExePath;

    public bool Success { get; private set; }

    public LoginPromptWindow(ClaudeCodeDependencyService dependencyService, string claudeExePath)
    {
        InitializeComponent();
        _dependencyService = dependencyService;
        _claudeExePath = claudeExePath;
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        StatusText.Text = "Waiting for login... (complete sign-in in the browser)";

        var success = await _dependencyService.LaunchLoginAsync(_claudeExePath,
            status => Dispatcher.Invoke(() => StatusText.Text = status));

        if (success)
        {
            Success = true;
            StatusText.Text = "Login successful!";
            await Task.Delay(500);
            DialogResult = true;
            Close();
        }
        else
        {
            StatusText.Text = "Login not detected. Please try again.";
            LoginButton.IsEnabled = true;
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        Success = false;
        DialogResult = false;
        Close();
    }
}
