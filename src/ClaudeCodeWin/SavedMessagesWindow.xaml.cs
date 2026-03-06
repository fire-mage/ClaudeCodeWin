using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin;

public partial class SavedMessagesWindow : Window
{
    private readonly List<SavedMessage> _messages;
    private readonly Action _onChanged;

    public SavedMessage? SelectedMessage { get; private set; }

    public SavedMessagesWindow(List<SavedMessage> messages, Action onChanged)
    {
        _messages = messages;
        _onChanged = onChanged;
        InitializeComponent();
        RefreshList();
    }

    private void RefreshList()
    {
        MessageList.ItemsSource = null;
        MessageList.ItemsSource = _messages.OrderByDescending(m => m.CreatedAt).ToList();
        EmptyMessage.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is SavedMessage msg)
        {
            SelectedMessage = msg;
            DialogResult = true;
            Close();
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is SavedMessage msg)
        {
            _messages.Remove(msg);
            _onChanged();
            RefreshList();
        }
    }

    private void MessageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MessageList.SelectedItem is SavedMessage msg)
        {
            SelectedMessage = msg;
            DialogResult = true;
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
