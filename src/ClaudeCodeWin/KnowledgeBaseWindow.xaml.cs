using System.Windows;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin;

public partial class KnowledgeBaseWindow : Window
{
    public KnowledgeBaseWindow(List<KnowledgeBaseEntry> entries)
    {
        InitializeComponent();

        if (entries.Count == 0)
        {
            EntryList.Visibility = Visibility.Collapsed;
            EmptyLabel.Visibility = Visibility.Visible;
        }
        else
        {
            // Show newest first
            EntryList.ItemsSource = entries.OrderByDescending(e => e.Date).ToList();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
