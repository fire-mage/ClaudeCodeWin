using System.Windows;
using System.Windows.Controls;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class ChatHistoryWindow : Window
{
    private readonly ChatHistoryService _historyService;

    public ChatHistoryEntry? SelectedEntry { get; private set; }

    public ChatHistoryWindow(ChatHistoryService historyService)
    {
        InitializeComponent();
        _historyService = historyService;

        LoadHistory();

        HistoryList.SelectionChanged += (_, _) =>
        {
            var hasSelection = HistoryList.SelectedItem is not null;
            OpenButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = hasSelection;
        };
    }

    private void LoadHistory()
    {
        HistoryList.ItemsSource = _historyService.ListAll();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not ChatHistorySummary summary) return;

        var entry = _historyService.Load(summary.Id);
        if (entry is null)
        {
            MessageBox.Show("Failed to load chat history.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedEntry = entry;
        DialogResult = true;
        Close();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not ChatHistorySummary summary) return;

        var result = MessageBox.Show(
            $"Delete chat \"{summary.Title}\"?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _historyService.Delete(summary.Id);
        LoadHistory();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
