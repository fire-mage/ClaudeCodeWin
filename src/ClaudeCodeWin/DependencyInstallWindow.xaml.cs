using System.Windows;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class DependencyInstallWindow : Window
{
    private readonly ClaudeCodeDependencyService _dependencyService;

    public bool Success { get; private set; }
    public string? ResolvedExePath { get; private set; }

    public DependencyInstallWindow(ClaudeCodeDependencyService dependencyService)
    {
        InitializeComponent();
        _dependencyService = dependencyService;
        Loaded += async (_, _) => await RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        var success = await _dependencyService.InstallAsync(OnProgress);
        Success = success;

        if (success)
        {
            var status = await _dependencyService.CheckAsync();
            ResolvedExePath = status.ExePath;

            StatusText.Text = "Installation complete!";
            StatusDot.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            StatusDot.Opacity = 1.0;
        }
        else
        {
            StatusText.Text = "Installation failed.";
            StatusDot.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            StatusDot.Opacity = 1.0;
        }

        // Stop pulsing animation
        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusDot.Opacity = 1.0;

        CloseButton.Visibility = Visibility.Visible;
    }

    private void OnProgress(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = message;

            if (LogText.Text.Length > 0)
                LogText.Text += "\n";
            LogText.Text += message;

            LogScroller.ScrollToEnd();
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = Success;
        Close();
    }
}
