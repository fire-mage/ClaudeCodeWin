using System.Windows;
using System.Windows.Media;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class ApiKeyDialog : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly List<ApiKeyEntry> _keys;
    private int _editIndex = -1; // -1 = adding new, >=0 = editing existing

    public ApiKeyDialog(AppSettings settings, SettingsService settingsService)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;

        // Deep-copy keys
        _keys = settings.ApiKeys.Select(k => k.Clone()).ToList();

        RefreshKeyList();
    }

    private void RefreshKeyList()
    {
        KeyList.ItemsSource = null;
        KeyList.ItemsSource = _keys.Select(k => new KeyListItem(k)).ToList();
    }

    private void KeyList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RemoveButton.IsEnabled = KeyList.SelectedItem is not null;
    }

    private void KeyList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (KeyList.SelectedItem is not KeyListItem) return;
        _editIndex = KeyList.SelectedIndex;
        var key = _keys[_editIndex];

        EditServiceName.Text = key.ServiceName;
        EditServiceId.Text = key.ServiceId;
        EditApiKey.Password = "";  // Don't pre-fill password (it's encrypted)
        EditExpiry.SelectedDate = key.ExpiresAt;
        EditWarningDays.Text = key.WarningDays.ToString();
        EditPanel.Visibility = Visibility.Visible;
    }

    private void AddKey_Click(object sender, RoutedEventArgs e)
    {
        _editIndex = -1;
        EditServiceName.Text = "";
        EditServiceId.Text = "";
        EditApiKey.Password = "";
        EditExpiry.SelectedDate = DateTime.Now.AddDays(90);
        EditWarningDays.Text = "14";
        EditPanel.Visibility = Visibility.Visible;
        EditServiceName.Focus();
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (KeyList.SelectedItem is not KeyListItem) return;
        var idx = KeyList.SelectedIndex;
        var key = _keys[idx];

        var result = MessageBox.Show(
            $"Remove API key for \"{key.ServiceName}\"?",
            "Confirm Removal",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _keys.RemoveAt(idx);
        SaveToSettings();
        RefreshKeyList();
    }

    private void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        var serviceName = EditServiceName.Text.Trim();
        var serviceId = EditServiceId.Text.Trim();

        if (string.IsNullOrEmpty(serviceName))
        {
            MessageBox.Show("Service name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(serviceId))
            serviceId = serviceName.ToLowerInvariant().Replace(' ', '-');

        // Check for duplicate ServiceId
        var duplicateIdx = _keys.FindIndex(k =>
            string.Equals(k.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase));
        if (duplicateIdx >= 0 && duplicateIdx != _editIndex)
        {
            MessageBox.Show($"A key with service ID \"{serviceId}\" already exists.",
                "Duplicate", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(EditWarningDays.Text.Trim(), out var warningDays) || warningDays < 0)
        {
            MessageBox.Show("Warning days must be a non-negative number.",
                "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApiKeyEntry entry;
        if (_editIndex >= 0)
        {
            entry = _keys[_editIndex];
        }
        else
        {
            entry = new ApiKeyEntry();
            _keys.Add(entry);
        }

        entry.ServiceId = serviceId;
        entry.ServiceName = serviceName;
        entry.ExpiresAt = EditExpiry.SelectedDate;
        entry.WarningDays = warningDays;

        // Only update key if user typed something new
        if (!string.IsNullOrEmpty(EditApiKey.Password))
        {
            var encrypted = SettingsService.Protect(EditApiKey.Password);
            if (string.IsNullOrEmpty(encrypted))
            {
                MessageBox.Show("Failed to encrypt API key.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            entry.KeyProtected = encrypted;
        }

        SaveToSettings();
        EditPanel.Visibility = Visibility.Collapsed;
        RefreshKeyList();
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        EditPanel.Visibility = Visibility.Collapsed;
    }

    private void SaveToSettings()
    {
        _settings.ApiKeys = _keys.Select(k => k.Clone()).ToList();
        _settingsService.Save(_settings);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private record KeyListItem
    {
        private static readonly Brush ExpiredBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)));
        private static readonly Brush WarningBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFA, 0xB3, 0x87)));
        private static readonly Brush ValidBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)));

        private static SolidColorBrush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

        public string ServiceName { get; }
        public string KeyMasked { get; }
        public string StatusText { get; }
        public Brush StatusBrush { get; }

        public KeyListItem(ApiKeyEntry key)
        {
            ServiceName = key.ServiceName;

            // Compute masked key once (avoids DPAPI call on every WPF render)
            if (string.IsNullOrEmpty(key.KeyProtected))
            {
                KeyMasked = "(no key)";
            }
            else
            {
                try
                {
                    var plain = SettingsService.Unprotect(key.KeyProtected);
                    if (plain.Length < 12)
                    {
                        KeyMasked = new string('*', plain.Length);
                    }
                    else
                    {
                        KeyMasked = plain[..4] + new string('*', plain.Length - 8) + plain[^4..];
                    }
                }
                catch
                {
                    KeyMasked = "(encrypted)";
                }
            }

            // Compute status once
            var (days, isExpired, isWarning) = key.GetExpiryStatus();
            if (!key.ExpiresAt.HasValue)
            {
                StatusText = "No expiry";
                StatusBrush = Brushes.Gray;
            }
            else if (isExpired)
            {
                StatusText = $"Expired {-days}d ago";
                StatusBrush = ExpiredBrush;
            }
            else if (isWarning)
            {
                StatusText = days == 0 ? "Expires today" : $"Expires in {days}d";
                StatusBrush = WarningBrush;
            }
            else
            {
                StatusText = $"Valid ({days}d left)";
                StatusBrush = ValidBrush;
            }
        }
    }
}
