using System.Windows;
using System.Windows.Media;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin;

public partial class DiffViewerWindow : Window
{
    public class DiffLineView
    {
        public string LineNumber { get; set; } = "";
        public string Indicator { get; set; } = "";
        public string Text { get; set; } = "";
        public Brush Background { get; set; } = Brushes.Transparent;
        public Brush IndicatorForeground { get; set; } = Brushes.Transparent;
    }

    public DiffViewerWindow(string filePath, List<DiffLine> lines)
    {
        InitializeComponent();
        Title = $"Changes: {System.IO.Path.GetFileName(filePath)}";
        FilePathText.Text = filePath;

        var addedCount = lines.Count(l => l.Type == DiffLineType.Added);
        var removedCount = lines.Count(l => l.Type == DiffLineType.Removed);
        StatsText.Text = $"+{addedCount} added, -{removedCount} removed, {lines.Count - addedCount - removedCount} unchanged";

        var addedBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x1E));
        var removedBg = new SolidColorBrush(Color.FromRgb(0x3A, 0x1E, 0x1E));
        var addedFg = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)); // green (SuccessColor)
        var removedFg = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)); // red (ErrorColor)

        addedBg.Freeze();
        removedBg.Freeze();
        addedFg.Freeze();
        removedFg.Freeze();

        var items = lines.Select(l => new DiffLineView
        {
            LineNumber = l.Type switch
            {
                DiffLineType.Added => l.NewLineNumber?.ToString() ?? "",
                DiffLineType.Removed => l.OldLineNumber?.ToString() ?? "",
                _ => l.NewLineNumber?.ToString() ?? ""
            },
            Indicator = l.Type switch
            {
                DiffLineType.Added => "+",
                DiffLineType.Removed => "-",
                _ => " "
            },
            Text = l.Text,
            Background = l.Type switch
            {
                DiffLineType.Added => addedBg,
                DiffLineType.Removed => removedBg,
                _ => Brushes.Transparent
            },
            IndicatorForeground = l.Type switch
            {
                DiffLineType.Added => addedFg,
                DiffLineType.Removed => removedFg,
                _ => Brushes.Transparent
            }
        }).ToList();

        DiffContent.ItemsSource = items;
    }
}
