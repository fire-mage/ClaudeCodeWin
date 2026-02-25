using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class MarketplaceWindow : Window
{
    private readonly MarketplaceService _marketplaceService;
    private readonly HashSet<string> _installedIds;
    private List<MarketplacePlugin> _allPlugins;
    private string? _activeTag;

    /// <summary>
    /// Plugin selected for installation via Explore Skill flow.
    /// </summary>
    public MarketplacePlugin? SelectedPlugin { get; private set; }

    public MarketplaceWindow(MarketplaceService marketplaceService, List<KnowledgeBaseEntry> kbEntries)
    {
        InitializeComponent();

        _marketplaceService = marketplaceService;
        _installedIds = marketplaceService.GetInstalledPluginIds(kbEntries);
        _allPlugins = marketplaceService.GetAllPlugins();

        BuildTagFilters();
        ApplyFilter();

        Loaded += (_, _) => SearchBox.Focus();
    }

    private void BuildTagFilters()
    {
        var allTags = _allPlugins
            .SelectMany(p => p.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();

        TagFilters.Items.Clear();

        // "All" button
        var allBtn = CreateTagButton("All", isActive: true);
        allBtn.Click += (_, _) =>
        {
            _activeTag = null;
            UpdateTagButtonStates();
            ApplyFilter();
        };
        TagFilters.Items.Add(allBtn);

        foreach (var tag in allTags)
        {
            var btn = CreateTagButton(tag, isActive: false);
            var capturedTag = tag;
            btn.Click += (_, _) =>
            {
                _activeTag = _activeTag == capturedTag ? null : capturedTag;
                UpdateTagButtonStates();
                ApplyFilter();
            };
            TagFilters.Items.Add(btn);
        }
    }

    private Button CreateTagButton(string text, bool isActive)
    {
        var bgBrush = isActive
            ? (Brush)FindResource("PrimaryBrush")
            : (Brush)FindResource("SurfaceLightBrush");
        var fgBrush = isActive
            ? Brushes.White
            : (Brush)FindResource("TextSecondaryBrush");

        return new Button
        {
            Content = text,
            Tag = text,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 4),
            FontSize = 11,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = bgBrush,
            Foreground = fgBrush,
            BorderThickness = new Thickness(0)
        };
    }

    private void UpdateTagButtonStates()
    {
        var primaryBrush = (Brush)FindResource("PrimaryBrush");
        var surfaceBrush = (Brush)FindResource("SurfaceLightBrush");
        var whiteBrush = Brushes.White;
        var secondaryBrush = (Brush)FindResource("TextSecondaryBrush");

        foreach (var item in TagFilters.Items)
        {
            if (item is not Button btn) continue;
            var tag = btn.Tag as string;

            bool isActive = (tag == "All" && _activeTag is null) ||
                            (tag != "All" && tag == _activeTag);

            btn.Background = isActive ? primaryBrush : surfaceBrush;
            btn.Foreground = isActive ? whiteBrush : secondaryBrush;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var filtered = _allPlugins.Where(p =>
        {
            // Tag filter
            if (_activeTag is not null &&
                !p.Tags.Any(t => t.Equals(_activeTag, StringComparison.OrdinalIgnoreCase)))
                return false;

            // Search filter
            if (query.Length > 0)
            {
                return p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       p.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       p.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            return true;
        }).ToList();

        PluginList.ItemsSource = filtered;

        EmptyLabel.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginList.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Update search placeholder
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var installedCount = _allPlugins.Count(p => _installedIds.Contains(p.Id));
        StatusText.Text = $"{_allPlugins.Count} plugins, {installedCount} installed";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string pluginId)
            return;

        var plugin = _allPlugins.FirstOrDefault(p => p.Id == pluginId);
        if (plugin is null) return;

        if (_installedIds.Contains(pluginId))
        {
            MessageBox.Show($"'{plugin.Name}' is already installed in the Knowledge Base.",
                "Already Installed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedPlugin = plugin;
        DialogResult = true;
    }

    private async void ImportUrl_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ImportUrlDialog { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.Url))
            return;

        try
        {
            StatusText.Text = "Importing...";
            var plugin = await _marketplaceService.ImportFromUrlAsync(dialog.Url, dialog.PluginName);
            if (plugin is null)
            {
                MessageBox.Show("Failed to import: empty content.", "Import Failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _allPlugins = _marketplaceService.GetAllPlugins();
            BuildTagFilters();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to import: {ex.Message}", "Import Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
