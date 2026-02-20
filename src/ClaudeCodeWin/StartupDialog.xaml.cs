using System.IO;
using System.Windows;
using System.Windows.Input;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class StartupDialog : Window
{
    private readonly ChatHistoryService _historyService;
    private readonly List<SessionDisplayItem> _sessions;

    /// <summary>
    /// The selected chat history entry, or null if user chose "New session".
    /// </summary>
    public ChatHistoryEntry? SelectedEntry { get; private set; }

    public StartupDialog(ChatHistoryService historyService)
    {
        InitializeComponent();
        _historyService = historyService;

        var summaries = historyService.ListAll();
        _sessions = summaries
            .Take(10)
            .Select(s => new SessionDisplayItem
            {
                Id = s.Id,
                Title = s.Title,
                ProjectPath = s.ProjectPath,
                ProjectName = ExtractProjectName(s.ProjectPath),
                UpdatedAt = s.UpdatedAt,
                MessageCount = s.MessageCount
            })
            .ToList();

        SessionList.ItemsSource = _sessions;
    }

    private static string ExtractProjectName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "No project";
        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
               ?? path;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AcceptSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Escape = new session
            SelectedEntry = null;
            DialogResult = true;
            Close();
            e.Handled = true;
        }
    }

    private void NewSession_Click(object sender, MouseButtonEventArgs e)
    {
        SessionList.SelectedItem = null;
        NewSessionOption.BorderBrush = (System.Windows.Media.Brush)FindResource("PrimaryBrush");
    }

    private void SessionList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not null)
            NewSessionOption.BorderBrush = System.Windows.Media.Brushes.Transparent;
        else
            NewSessionOption.BorderBrush = (System.Windows.Media.Brush)FindResource("PrimaryBrush");
    }

    private void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionList.SelectedItem is SessionDisplayItem)
            AcceptSelection();
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        AcceptSelection();
    }

    private void AcceptSelection()
    {
        if (SessionList.SelectedItem is SessionDisplayItem item)
        {
            SelectedEntry = _historyService.Load(item.Id);
        }
        else
        {
            SelectedEntry = null; // New session
        }

        DialogResult = true;
        Close();
    }
}

/// <summary>
/// Display model for session list items.
/// </summary>
public class SessionDisplayItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? ProjectPath { get; set; }
    public string ProjectName { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public int MessageCount { get; set; }
}
