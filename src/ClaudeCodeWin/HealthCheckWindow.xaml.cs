using System.Text;
using System.Windows;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class HealthCheckWindow : Window
{
    private readonly HealthCheckService _healthCheckService;
    private readonly string? _workingDirectory;
    private List<HealthCheckViewModel>? _lastResults;

    public HealthCheckWindow(HealthCheckService healthCheckService, string? workingDirectory)
    {
        InitializeComponent();
        _healthCheckService = healthCheckService;
        _workingDirectory = workingDirectory;

        Loaded += async (_, _) => await RunChecksAsync();
    }

    private async Task RunChecksAsync()
    {
        LoadingLabel.Visibility = Visibility.Visible;
        ResultList.Visibility = Visibility.Collapsed;
        RecheckButton.IsEnabled = false;
        CopyButton.IsEnabled = false;

        try
        {
            var results = await _healthCheckService.RunAllChecksAsync(_workingDirectory);
            _lastResults = results.Select(r => new HealthCheckViewModel(r)).ToList();

            // Guard UI access after await — window may have closed during RunAllChecksAsync
            if (!IsLoaded) return;
            ResultList.ItemsSource = _lastResults;
            ResultList.Visibility = Visibility.Visible;
            LoadingLabel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // FIX: guard against accessing disposed controls if window closed during async operation
            if (IsLoaded)
                LoadingLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            if (IsLoaded)
            {
                RecheckButton.IsEnabled = true;
                CopyButton.IsEnabled = true;
            }
        }
    }

    private async void RecheckButton_Click(object sender, RoutedEventArgs e)
    {
        // FIX: try-catch for async void — protects against ObjectDisposedException if window is closing
        try { await RunChecksAsync(); }
        catch (Exception ex) { if (IsLoaded) LoadingLabel.Text = $"Error: {ex.Message}"; }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResults is null) return;

        var sb = new StringBuilder();
        sb.AppendLine("CCW Health Check Report");
        sb.AppendLine(new string('─', 50));
        foreach (var r in _lastResults)
        {
            sb.AppendLine($"{r.StatusIcon}  {r.Name,-20} {r.Details}");
        }
        sb.AppendLine(new string('─', 50));
        sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        Clipboard.SetText(sb.ToString());

        // Brief visual feedback
        var original = CopyButton.Content;
        CopyButton.Content = "Copied!";
        // FIX: use BeginInvoke instead of Invoke - avoids potential hang if window closes during delay
        _ = Task.Delay(1500).ContinueWith(_ =>
            Dispatcher.BeginInvoke(() => { if (IsLoaded) CopyButton.Content = original; }));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public class HealthCheckViewModel
{
    public string StatusIcon { get; }
    public string Name { get; }
    public string Details { get; }

    public HealthCheckViewModel(HealthCheckResult result)
    {
        Name = result.Name;
        Details = result.Details;
        StatusIcon = result.Status switch
        {
            HealthStatus.OK => "✓",
            HealthStatus.Warning => "⚠",
            HealthStatus.Error => "✗",
            _ => "…",
        };
    }
}
