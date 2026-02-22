using System.IO;
using System.Reflection;
using System.Windows;

namespace ClaudeCodeWin;

public partial class AboutWindow : Window
{
    private const string Email = "claudecodewin.support@main.fish";

    public AboutWindow()
    {
        InitializeComponent();

        var infoVersion = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        var parts = infoVersion.Split('+');
        var version = parts[0];
        var buildHash = parts.Length > 1 ? parts[1][..Math.Min(7, parts[1].Length)] : "";

        var exePath = Environment.ProcessPath ?? "";
        var buildDate = !string.IsNullOrEmpty(exePath) && File.Exists(exePath)
            ? File.GetLastWriteTime(exePath).ToString("yyyy-MM-dd")
            : "unknown";

        TitleText.Text = $"ClaudeCodeWin v{version}";

        var buildInfo = $"Built: {buildDate}";
        if (!string.IsNullOrEmpty(buildHash))
            buildInfo += $"  |  Build: {buildHash}";
        BuildText.Text = buildInfo;

        EmailText.Text = $"Support: {Email}";
    }

    private void CopyEmail_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(Email);
        CopyEmailButton.Content = "Copied!";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
