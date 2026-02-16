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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
