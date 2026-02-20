using System.Windows;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin;

public partial class ServerEditDialog : Window
{
    public ServerInfo Server { get; private set; } = new();

    public ServerEditDialog()
    {
        InitializeComponent();
    }

    public ServerEditDialog(ServerInfo existing) : this()
    {
        NameBox.Text = existing.Name;
        HostBox.Text = existing.Host;
        PortBox.Text = existing.Port.ToString();
        UserBox.Text = existing.User;
        DescriptionBox.Text = existing.Description ?? "";
        ProjectsBox.Text = string.Join(", ", existing.Projects);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(HostBox.Text))
        {
            MessageBox.Show("Name and Host are required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PortBox.Text, out var port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Port must be a number between 1 and 65535.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Server = new ServerInfo
        {
            Name = NameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Port = port,
            User = string.IsNullOrWhiteSpace(UserBox.Text) ? "root" : UserBox.Text.Trim(),
            Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
            Projects = ProjectsBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
        };

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
