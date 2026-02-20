using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;
using Microsoft.Win32;

namespace ClaudeCodeWin;

public partial class ServerRegistryWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly List<ServerInfo> _servers;

    public ServerRegistryWindow(AppSettings settings, SettingsService settingsService)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;

        // Deep-copy servers so cancel doesn't affect originals
        _servers = settings.Servers.Select(s => new ServerInfo
        {
            Name = s.Name,
            Host = s.Host,
            Port = s.Port,
            User = s.User,
            Description = s.Description,
            Projects = [.. s.Projects]
        }).ToList();

        SshKeyPathBox.Text = settings.SshKeyPath ?? "";
        SshMasterPasswordBox.Password = SettingsService.Unprotect(settings.SshMasterPasswordProtected ?? "");
        RefreshServerList();
    }

    private void RefreshServerList()
    {
        ServerList.ItemsSource = null;
        ServerList.ItemsSource = _servers.Select(s => new ServerListItem(s)).ToList();
    }

    private void BrowseSshKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select SSH Private Key",
            Filter = "All Files (*.*)|*.*|PEM Files (*.pem)|*.pem",
            InitialDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh")
        };

        if (dlg.ShowDialog(this) == true)
            SshKeyPathBox.Text = dlg.FileName;
    }

    private void ClearSshKey_Click(object sender, RoutedEventArgs e)
    {
        SshKeyPathBox.Text = "";
    }

    private void ClearSshPassword_Click(object sender, RoutedEventArgs e)
    {
        SshMasterPasswordBox.Password = "";
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ServerEditDialog { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _servers.Add(dlg.Server);
            RefreshServerList();
        }
    }

    private void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerListItem item) return;
        var idx = ServerList.SelectedIndex;
        _servers.RemoveAt(idx);
        RefreshServerList();
    }

    private void ServerList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var hasSelection = ServerList.SelectedItem is not null;
        RemoveButton.IsEnabled = hasSelection;
        TestButton.IsEnabled = hasSelection;
    }

    private async void TestServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerListItem item) return;
        var idx = ServerList.SelectedIndex;
        var server = _servers[idx];
        var sshKey = SshKeyPathBox.Text;

        TestButton.IsEnabled = false;
        TestButton.Content = "Testing...";

        try
        {
            var args = new StringBuilder();
            if (!string.IsNullOrEmpty(sshKey))
                args.Append($"-i \"{sshKey}\" ");
            args.Append($"-p {server.Port} ");
            args.Append("-o ConnectTimeout=5 -o StrictHostKeyChecking=no -o BatchMode=yes ");
            args.Append($"{server.User}@{server.Host} \"echo ok\"");

            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = args.ToString(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = Process.Start(psi);
            if (process is null)
            {
                MessageBox.Show("Failed to start ssh process.", "Test Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode == 0 && stdout.Trim() == "ok")
            {
                MessageBox.Show($"Connection to {server.Name} ({server.Host}) successful!",
                    "Test Passed", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var msg = $"Connection failed (exit code {process.ExitCode}).\n\n";
                if (!string.IsNullOrEmpty(stderr))
                    msg += stderr.Trim();
                else if (!string.IsNullOrEmpty(stdout))
                    msg += stdout.Trim();
                MessageBox.Show(msg, "Test Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("Connection timed out (10 seconds).", "Test Failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Test Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestButton.Content = "Test";
            TestButton.IsEnabled = ServerList.SelectedItem is not null;
        }
    }

    private void ServerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerListItem item) return;
        var idx = ServerList.SelectedIndex;

        var dlg = new ServerEditDialog(_servers[idx]) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _servers[idx] = dlg.Server;
            RefreshServerList();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.SshKeyPath = string.IsNullOrWhiteSpace(SshKeyPathBox.Text) ? null : SshKeyPathBox.Text;
        _settings.SshMasterPassword = null; // Always null on disk (legacy field)
        _settings.SshMasterPasswordProtected = string.IsNullOrEmpty(SshMasterPasswordBox.Password)
            ? null
            : SettingsService.Protect(SshMasterPasswordBox.Password);
        _settings.Servers = _servers;
        _settingsService.Save(_settings);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private record ServerListItem(ServerInfo Server)
    {
        public string Name => Server.Name;
        public string ConnectionString => $"{Server.User}@{Server.Host}:{Server.Port}";
        public string Details
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(Server.Description))
                    parts.Add(Server.Description);
                if (Server.Projects.Count > 0)
                    parts.Add($"Projects: {string.Join(", ", Server.Projects)}");
                return string.Join(" | ", parts);
            }
        }
    }
}
