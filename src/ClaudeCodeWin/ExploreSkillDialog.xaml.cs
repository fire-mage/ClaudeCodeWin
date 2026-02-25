using System.Windows;

namespace ClaudeCodeWin;

public partial class ExploreSkillDialog : Window
{
    public string? UserInput { get; private set; }

    public ExploreSkillDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => InputBox.Focus();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        UserInput = text;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
