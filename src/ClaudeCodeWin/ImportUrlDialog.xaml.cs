using System.Windows;

namespace ClaudeCodeWin;

public partial class ImportUrlDialog : Window
{
    public string? Url { get; private set; }
    public string? PluginName { get; private set; }

    public ImportUrlDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => UrlBox.Focus();
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(url))
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            MessageBox.Show("Please enter a valid HTTP(S) URL.", "Invalid URL",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Url = url;
        PluginName = string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
