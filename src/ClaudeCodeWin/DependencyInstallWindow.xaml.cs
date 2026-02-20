using System.Windows;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class DependencyInstallWindow : Window
{
    private readonly Func<Action<string>?, Task<bool>> _installFunc;
    private readonly Func<Task>? _postInstall;

    public bool Success { get; private set; }
    public string? ResolvedExePath { get; private set; }

    /// <summary>
    /// Constructor for Claude Code CLI installation (legacy).
    /// </summary>
    public DependencyInstallWindow(ClaudeCodeDependencyService dependencyService)
        : this("Installing Claude Code CLI...", dependencyService.InstallAsync)
    {
        _postInstall = async () =>
        {
            var status = await dependencyService.CheckAsync();
            ResolvedExePath = status.ExePath;
        };
    }

    /// <summary>
    /// Generic constructor for any dependency installation with progress.
    /// </summary>
    public DependencyInstallWindow(string title, Func<Action<string>?, Task<bool>> installFunc, Func<Task>? postInstall = null)
    {
        InitializeComponent();
        _installFunc = installFunc;
        _postInstall = postInstall;
        TitleText.Text = title;
        Loaded += async (_, _) => await RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        var success = await _installFunc(OnProgress);
        Success = success;

        if (success)
        {
            if (_postInstall is not null)
                await _postInstall();

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
