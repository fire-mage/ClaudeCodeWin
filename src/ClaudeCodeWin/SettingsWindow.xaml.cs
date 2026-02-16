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
    private bool _initialized;

    public SettingsWindow(AppSettings settings, SettingsService settingsService, MainViewModel viewModel)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;
        _viewModel = viewModel;

        // Set current state
        if (settings.UpdateChannel == "beta")
            BetaRadio.IsChecked = true;
        else
            StableRadio.IsChecked = true;

        AutoConfirmCheck.IsChecked = settings.AutoConfirmPlanMode;
        ContextSnapshotCheck.IsChecked = settings.ContextSnapshotEnabled;

        UpdateServersSummary();

        _initialized = true;
    }

    private void UpdateChannel_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;

        var channel = BetaRadio.IsChecked == true ? "beta" : "stable";
        _settings.UpdateChannel = channel;
        _settingsService.Save(_settings);
        _viewModel.SetUpdateChannel(channel);
    }

    private void AutoConfirm_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;

        _viewModel.AutoConfirmEnabled = AutoConfirmCheck.IsChecked == true;
    }

    private void ContextSnapshot_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;

        _settings.ContextSnapshotEnabled = ContextSnapshotCheck.IsChecked == true;
        _settingsService.Save(_settings);
    }

    private void ExpandContext_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ExpandContextCommand.Execute(null);
        Close();
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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
