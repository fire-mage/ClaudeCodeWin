using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class MarketplaceWindow : Window
{
    private readonly MarketplaceService _marketplaceService;
    private readonly McpRegistryService _registryService;
    private readonly HashSet<string> _installedIds;
    private List<MarketplacePlugin> _allPlugins;
    private string? _activeTag;

    // MCP tab state
    private List<McpRegistryServer> _mcpServers = [];
    private CancellationTokenSource? _searchCts;
    private DispatcherTimer? _searchDebounceTimer;
    private bool _mcpTabLoaded;
    private bool _mcpTabLoading;

    /// <summary>
    /// Plugin selected for installation via Explore Skill flow.
    /// </summary>
    public MarketplacePlugin? SelectedPlugin { get; private set; }

    /// <summary>
    /// MCP server selected for installation.
    /// </summary>
    public McpRegistryServer? SelectedMcpServer { get; private set; }

    /// <summary>
    /// True when the user selected an MCP server (not a knowledge plugin).
    /// </summary>
    public bool IsMcpInstall { get; private set; }

    /// <summary>
    /// True when the user clicked "Ask Claude" for a recommendation.
    /// </summary>
    public bool IsRecommendationRequest { get; private set; }

    /// <summary>
    /// The user's goal/problem description for the recommendation.
    /// </summary>
    public string? UserGoal { get; private set; }

    /// <summary>
    /// The list of MCP servers to evaluate for the recommendation.
    /// </summary>
    public List<McpRegistryServer>? RecommendationServers { get; private set; }

    public MarketplaceWindow(MarketplaceService marketplaceService, McpRegistryService registryService, List<KnowledgeBaseEntry> kbEntries)
    {
        InitializeComponent();

        _marketplaceService = marketplaceService;
        _registryService = registryService;
        _installedIds = marketplaceService.GetInstalledPluginIds(kbEntries);
        _allPlugins = marketplaceService.GetAllPlugins();

        BuildTagFilters();
        ApplyFilter();

        Loaded += async (_, _) =>
        {
            McpSearchBox.Focus();
            // MCP is now Tab 0 (default) — load servers on first open
            if (!_mcpTabLoaded && !_mcpTabLoading)
            {
                _mcpTabLoading = true;
                try { await LoadMcpServersAsync(); _mcpTabLoaded = true; }
                catch { /* Allow retry on next tab switch */ }
                finally { _mcpTabLoading = false; }
                UpdateBottomStatus();
            }
        };
    }

    // ===== Tab switching =====

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard: fires during InitializeComponent before fields are assigned
        if (_registryService is null) return;

        if (MainTabs.SelectedIndex == 0 && !_mcpTabLoaded && !_mcpTabLoading)
        {
            _mcpTabLoading = true;
            try
            {
                await LoadMcpServersAsync();
                _mcpTabLoaded = true;
            }
            catch
            {
                // Allow retry on next tab switch
            }
            finally
            {
                _mcpTabLoading = false;
            }
        }

        UpdateBottomStatus();
    }

    // ===== Knowledge Skills tab (existing logic) =====

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
            if (_activeTag is not null &&
                !p.Tags.Any(t => t.Equals(_activeTag, StringComparison.OrdinalIgnoreCase)))
                return false;

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

    // ===== MCP Servers tab =====

    private async Task LoadMcpServersAsync()
    {
        McpStatusText.Text = "Loading MCP servers...";
        McpRefreshButton.IsEnabled = false;

        try
        {
            var (servers, fromCache) = await _registryService.GetCachedOrFetchAsync();
            _mcpServers = servers;
            UpdateMcpList();

            var cacheNote = fromCache ? " (cached)" : "";
            McpStatusText.Text = $"{_mcpServers.Count} servers{cacheNote} \u00b7 Search for more";
        }
        catch (Exception ex)
        {
            McpStatusText.Text = $"Failed to load: {ex.Message}";
        }
        finally
        {
            McpRefreshButton.IsEnabled = true;
        }
    }

    private void UpdateMcpList()
    {
        McpServerList.ItemsSource = _mcpServers;
        McpEmptyLabel.Visibility = _mcpServers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        McpServerList.Visibility = _mcpServers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        McpSearchPlaceholder.Visibility = string.IsNullOrEmpty(McpSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateAskClaudeButton();

        // Schedule button state update after layout — Loaded event may not fire
        // reliably when ItemsSource changes (WPF VirtualizingPanel quirk)
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdateMcpButtonStates);
    }

    private void UpdateMcpButtonStates()
    {
        for (var i = 0; i < McpServerList.Items.Count; i++)
        {
            if (McpServerList.ItemContainerGenerator.ContainerFromIndex(i) is not System.Windows.Controls.ListViewItem container)
                continue;
            var btn = FindDescendant<Button>(container);
            if (btn is not null && btn.Tag is string serverName)
                ApplyMcpButtonState(btn, serverName);
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var result = FindDescendant<T>(child);
            if (result is not null) return result;
        }
        return null;
    }

    private void McpSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        McpSearchPlaceholder.Visibility = string.IsNullOrEmpty(McpSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateAskClaudeButton();

        _searchDebounceTimer?.Stop();
        _searchDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _searchDebounceTimer.Tick -= OnSearchDebounce;
        _searchDebounceTimer.Tick += OnSearchDebounce;
        _searchDebounceTimer.Start();
    }

    private async void OnSearchDebounce(object? sender, EventArgs e)
    {
        _searchDebounceTimer?.Stop();
        var query = McpSearchBox.Text?.Trim() ?? "";

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        try
        {
            McpStatusText.Text = string.IsNullOrEmpty(query) ? "Loading..." : $"Searching \"{query}\"...";

            List<McpRegistryServer> results;
            if (string.IsNullOrEmpty(query))
            {
                var (servers, _) = await _registryService.GetCachedOrFetchAsync(ct);
                results = servers;
            }
            else
            {
                results = await _registryService.SearchAsync(query, ct);
            }

            if (!ct.IsCancellationRequested)
            {
                _mcpServers = results;
                UpdateMcpList();
                McpStatusText.Text = string.IsNullOrEmpty(query)
                    ? $"{_mcpServers.Count} servers \u00b7 Search for more"
                    : $"{_mcpServers.Count} results for \"{query}\"";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                McpStatusText.Text = $"Search failed: {ex.Message}";
        }
    }

    private async void McpRefresh_Click(object sender, RoutedEventArgs e)
    {
        McpStatusText.Text = "Refreshing...";
        McpRefreshButton.IsEnabled = false;

        try
        {
            _mcpServers = await _registryService.RefreshCacheAsync();
            UpdateMcpList();
            McpStatusText.Text = $"{_mcpServers.Count} servers \u00b7 Search for more";
        }
        catch (Exception ex)
        {
            McpStatusText.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            McpRefreshButton.IsEnabled = true;
        }
    }

    private void McpInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string serverName)
            return;

        var server = _mcpServers.FirstOrDefault(s => s.Name == serverName);
        if (server is null) return;

        var kbTag = Services.McpRegistryService.GetKbTag(server);
        if (_installedIds.Contains(kbTag))
        {
            MessageBox.Show($"'{server.DisplayName}' is already installed and documented in your Knowledge Base.",
                "Already Installed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedMcpServer = server;
        IsMcpInstall = true;
        DialogResult = true;
    }

    private void McpInstallButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string serverName) return;
        ApplyMcpButtonState(btn, serverName);
    }

    private void ApplyMcpButtonState(Button btn, string serverName)
    {
        var server = _mcpServers.FirstOrDefault(s => s.Name == serverName);
        if (server is not null && _installedIds.Contains(Services.McpRegistryService.GetKbTag(server)))
            MarkButtonAsInstalled(btn);
        else
            ResetButtonToInstall(btn);
    }

    private void PluginInstallButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string pluginId) return;
        if (_installedIds.Contains(pluginId))
            MarkButtonAsInstalled(btn);
        else
            ResetButtonToInstall(btn);
    }

    private static void MarkButtonAsInstalled(Button btn)
    {
        btn.Content = "Installed";
        btn.IsHitTestVisible = false;
        btn.Opacity = 0.6;
        btn.Background = System.Windows.Media.Brushes.Transparent;
        btn.BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("PrimaryBrush");
        btn.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("PrimaryBrush");
        btn.BorderThickness = new Thickness(1);
    }

    private static void ResetButtonToInstall(Button btn)
    {
        btn.ClearValue(ContentControl.ContentProperty);
        btn.ClearValue(UIElement.IsHitTestVisibleProperty);
        btn.ClearValue(UIElement.OpacityProperty);
        btn.ClearValue(Control.BackgroundProperty);
        btn.ClearValue(Control.BorderBrushProperty);
        btn.ClearValue(Control.ForegroundProperty);
        btn.ClearValue(Control.BorderThicknessProperty);
    }

    // ===== Ask Claude recommendation =====

    private void GoalBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        GoalPlaceholder.Visibility = string.IsNullOrEmpty(GoalBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateAskClaudeButton();
    }

    private void UpdateAskClaudeButton()
    {
        AskClaudeButton.IsEnabled = !string.IsNullOrWhiteSpace(GoalBox.Text)
            && _mcpServers.Count > 0
            && !string.IsNullOrWhiteSpace(McpSearchBox.Text);
    }

    private void AskClaude_Click(object sender, RoutedEventArgs e)
    {
        var goal = GoalBox.Text?.Trim();
        if (string.IsNullOrEmpty(goal) || _mcpServers.Count == 0) return;

        IsRecommendationRequest = true;
        UserGoal = goal;
        RecommendationServers = _mcpServers.ToList();
        DialogResult = true;
    }

    // ===== Common =====

    private void UpdateBottomStatus()
    {
        if (MainTabs.SelectedIndex == 0)
        {
            var mcpInstalled = _mcpServers.Count(s =>
                _installedIds.Contains(Services.McpRegistryService.GetKbTag(s)));
            StatusText.Text = mcpInstalled > 0
                ? $"{_mcpServers.Count} MCP servers, {mcpInstalled} installed"
                : $"{_mcpServers.Count} MCP servers";
        }
        else
        {
            var installedCount = _allPlugins.Count(p => _installedIds.Contains(p.Id));
            StatusText.Text = $"{_allPlugins.Count} plugins, {installedCount} installed";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchDebounceTimer?.Stop();
        base.OnClosed(e);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
