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

        // FIX: wrap in try-catch - unhandled exception in async void crashes app
        try
        {
            var success = await _dependencyService.LaunchLoginAsync(_claudeExePath,
                // FIX: BeginInvoke instead of Invoke - avoids hang if window closes during callback
                status => Dispatcher.BeginInvoke(() => { if (IsLoaded) StatusText.Text = status; }));

            // FIX: guard UI access after await - window may have closed during login
            if (!IsLoaded) return;

            if (success)
            {
                Success = true;
                StatusText.Text = "Login successful!";
                await Task.Delay(500);
                // FIX: guard against window closed during delay - setting DialogResult on closed window throws
                if (IsLoaded)
                {
                    DialogResult = true;
                    Close();
                }
            }
            else
            {
                StatusText.Text = "Login not detected. Please try again.";
                LoginButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            // FIX: guard UI access - window may have closed during login
            if (IsLoaded)
            {
                StatusText.Text = $"Login error: {ex.Message}";
                LoginButton.IsEnabled = true;
            }
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        Success = false;
        DialogResult = false;
        Close();
    }
}
