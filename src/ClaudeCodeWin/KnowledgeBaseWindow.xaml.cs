using System.Windows;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin;

/// <summary>Unified display item for both local KB entries and developer articles.</summary>
public class KbDisplayItem
{
    public string Date { get; set; } = "";
    public string Source { get; set; } = "";
    public string WhenToRead { get; set; } = "";
    public string TagsDisplay { get; set; } = "";
    public bool IsRequired { get; set; }
}

public partial class KnowledgeBaseWindow : Window
{
    public KnowledgeBaseWindow(List<KnowledgeBaseEntry> localEntries, List<DevKbArticle> devArticles)
    {
        InitializeComponent();

        var items = new List<KbDisplayItem>();

        // Developer articles first
        foreach (var a in devArticles)
        {
            items.Add(new KbDisplayItem
            {
                Date = "dev team",
                Source = a.IsRequired ? "required" : "dev",
                WhenToRead = a.Title + (string.IsNullOrEmpty(a.WhenToRead) ? "" : " — " + a.WhenToRead),
                TagsDisplay = a.Tags.Count > 0 ? string.Join(", ", a.Tags) : "",
                IsRequired = a.IsRequired
            });
        }

        // Local (project) articles, newest first
        foreach (var e in localEntries.OrderByDescending(e => e.Date))
        {
            items.Add(new KbDisplayItem
            {
                Date = e.Date.ToString("MMM dd, HH:mm"),
                Source = e.Source,
                WhenToRead = e.WhenToRead,
                TagsDisplay = e.TagsDisplay
            });
        }

        if (items.Count == 0)
        {
            EntryList.Visibility = Visibility.Collapsed;
            EmptyLabel.Visibility = Visibility.Visible;
        }
        else
        {
            EntryList.ItemsSource = items;
        }

        // Update subtitle to reflect both sources
        if (devArticles.Count > 0 && localEntries.Count > 0)
            SubtitleLabel.Text = $"{devArticles.Count} developer articles + {localEntries.Count} project articles.";
        else if (devArticles.Count > 0)
            SubtitleLabel.Text = $"{devArticles.Count} developer articles. No project articles yet.";
        else
            SubtitleLabel.Text = "Articles Claude has curated for this project. Claude manages this list automatically.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
