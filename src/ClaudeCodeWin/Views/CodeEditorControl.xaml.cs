using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services.Highlighting;

namespace ClaudeCodeWin.Views;

public partial class CodeEditorControl : UserControl
{
    private ScrollViewer? _editorScrollViewer;
    private int _currentMatchIndex = -1;
    private List<int> _matchPositions = [];
    private bool _suppressSearchUpdate;

    // Syntax highlighting state
    private ILanguageTokenizer? _tokenizer;
    private List<SyntaxToken> _cachedTokens = [];
    private int[] _lineStarts = [0];
    private bool _highlightingActive;
    private DispatcherTimer? _highlightTimer;
    private (int pos, int matchPos) _bracketHighlight = (-1, -1);
    private bool _fontMetricsMeasured;

    // IntelliSense state
    private ICompletionProvider? _completionProvider;
    private List<CompletionItem> _allCompletionItems = [];
    private int _completionAnchor = -1;
    private bool _completionActive;
    private DispatcherTimer? _autoTriggerTimer;
    private bool _suppressCompletionTrigger;

    public CodeEditorControl()
    {
        InitializeComponent();
        Editor.Loaded += (_, _) =>
        {
            HookScrollViewer();
            MeasureFontMetrics();
        };
        Editor.LostKeyboardFocus += (_, _) => DismissCompletion();
        // FIX: stop timers on unload to prevent leaks and stale callbacks
        Unloaded += (_, _) =>
        {
            _highlightTimer?.Stop();
            _autoTriggerTimer?.Stop();
            DismissCompletion();
        };
    }

    // Dependency Property: Text (two-way binding to SubTab.Content)
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(CodeEditorControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextPropertyChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (CodeEditorControl)d;
        var newText = (string?)e.NewValue ?? "";

        // Avoid re-entrant updates
        if (control.Editor.Text != newText)
            control.Editor.Text = newText;
    }

    // Dependency Property: FilePath (determines language for syntax highlighting)
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(CodeEditorControl),
        new PropertyMetadata(null, OnFilePathChanged));

    public string? FilePath
    {
        get => (string?)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    private static void OnFilePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (CodeEditorControl)d;
        var path = e.NewValue as string;
        control._tokenizer = LanguageDetector.GetTokenizer(path);
        control._completionProvider = LanguageDetector.GetCompletionProvider(path);
        control._highlightingActive = control._tokenizer != null;

        // Toggle foreground transparency for syntax highlighting
        if (control._highlightingActive)
        {
            control.Editor.Foreground = Brushes.Transparent;
            control.Editor.SelectionTextBrush = Brushes.Transparent;
        }
        else
        {
            control.Editor.Foreground = (Brush)control.FindResource("TextBrush");
            control.Editor.SelectionTextBrush = null; // use default

            // Clear stale highlight layer to prevent ghost rendering
            control._cachedTokens = [];
            control._bracketHighlight = (-1, -1);
            control.HighlightLayer.UpdateState("", [], [0], (-1, -1), 0, 0, 0);
            control.HighlightLayer.InvalidateHighlighting();
            control.DismissCompletion();
        }

        control.UpdateSyntaxHighlighting();
    }

    #region Syntax Highlighting

    private void MeasureFontMetrics()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var typeface = new Typeface(Editor.FontFamily, Editor.FontStyle,
            Editor.FontWeight, Editor.FontStretch);

        var ft = new FormattedText("M",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Editor.FontSize,
            Brushes.White,
            dpi.PixelsPerDip);

        HighlightLayer.SetFontMetrics(typeface, Editor.FontSize, ft.Height, ft.WidthIncludingTrailingWhitespace, Editor.Padding);
        _fontMetricsMeasured = true;

        // Trigger initial highlighting if FilePath was set before Loaded
        if (_highlightingActive)
            UpdateSyntaxHighlighting();
    }

    private void ScheduleHighlightUpdate()
    {
        if (!_highlightingActive) return;

        if (_highlightTimer == null)
        {
            _highlightTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _highlightTimer.Tick += HighlightTimer_Tick;
        }

        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    private void HighlightTimer_Tick(object? sender, EventArgs e)
    {
        _highlightTimer!.Stop();
        UpdateSyntaxHighlighting();
    }

    private void UpdateSyntaxHighlighting()
    {
        if (!_highlightingActive || _tokenizer == null || !_fontMetricsMeasured)
            return;

        var text = Editor.Text ?? "";
        _cachedTokens = _tokenizer.Tokenize(text);
        RebuildLineStarts(text);
        UpdateBracketHighlight();
        SyncHighlightLayer();
    }

    private void RebuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }
        _lineStarts = starts.ToArray();
    }

    private void SyncHighlightLayer()
    {
        if (!_highlightingActive || _editorScrollViewer == null)
            return;

        HighlightLayer.UpdateState(
            Editor.Text ?? "",
            _cachedTokens,
            _lineStarts,
            _bracketHighlight,
            _editorScrollViewer.VerticalOffset,
            _editorScrollViewer.HorizontalOffset,
            _editorScrollViewer.ViewportHeight);

        HighlightLayer.InvalidateHighlighting();
    }

    private void UpdateBracketHighlight()
    {
        var text = Editor.Text ?? "";
        var caret = Editor.CaretIndex;
        int bracketPos = -1;
        int matchPos = -1;

        // Check character at caret and before caret
        if (caret < text.Length && IsBracketChar(text[caret]))
        {
            bracketPos = caret;
            matchPos = BracketMatcher.FindMatch(text, caret, _cachedTokens);
        }
        else if (caret > 0 && IsBracketChar(text[caret - 1]))
        {
            bracketPos = caret - 1;
            matchPos = BracketMatcher.FindMatch(text, caret - 1, _cachedTokens);
        }

        _bracketHighlight = (bracketPos, matchPos);
    }

    private static bool IsBracketChar(char c) => c is '(' or ')' or '[' or ']' or '{' or '}';

    #endregion

    #region Line Numbers

    private void HookScrollViewer()
    {
        _editorScrollViewer = FindScrollViewer(Editor);
        if (_editorScrollViewer != null)
            _editorScrollViewer.ScrollChanged += EditorScrollChanged;

        UpdateLineNumbers();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject obj)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is ScrollViewer sv)
                return sv;
            var found = FindScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }

    private void EditorScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        SyncLineNumbersScroll();
        SyncHighlightLayer();

        if (_completionActive)
            DismissCompletion();
    }

    private void SyncLineNumbersScroll()
    {
        if (_editorScrollViewer == null) return;

        // Offset the line numbers TextBlock to match vertical scroll
        Canvas.SetTop(LineNumbers, -_editorScrollViewer.VerticalOffset);
    }

    private void UpdateLineNumbers()
    {
        var text = Editor.Text ?? "";
        var lineCount = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') lineCount++;
        }

        // Determine gutter width based on max line number digits
        var digits = Math.Max(2, lineCount.ToString().Length);
        var gutterWidth = digits * 9 + 20; // ~9px per digit + padding
        LineNumbers.Width = gutterWidth;

        // Build line numbers string
        var sb = new System.Text.StringBuilder(lineCount * 4);
        for (int i = 1; i <= lineCount; i++)
        {
            sb.AppendLine(i.ToString());
        }

        LineNumbers.Text = sb.ToString();
        SyncLineNumbersScroll();
    }

    #endregion

    #region Auto-indent & Tab→Spaces

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // IntelliSense keyboard handling (highest priority when popup is open)
        if (_completionActive && CompletionPopup.IsOpen)
        {
            if (e.Key == Key.Down)
            {
                CompletionList.SelectedIndex =
                    Math.Min(CompletionList.SelectedIndex + 1, CompletionList.Items.Count - 1);
                CompletionList.ScrollIntoView(CompletionList.SelectedItem);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Up)
            {
                CompletionList.SelectedIndex = Math.Max(CompletionList.SelectedIndex - 1, 0);
                CompletionList.ScrollIntoView(CompletionList.SelectedItem);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Tab || e.Key == Key.Return)
            {
                AcceptCompletion();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                DismissCompletion();
                e.Handled = true;
                return;
            }
        }

        // Ctrl+Space → manual IntelliSense trigger
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            TriggerCompletion(CompletionTrigger.Manual);
            e.Handled = true;
            return;
        }

        // Tab → 4 spaces
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            InsertAtCaret("    ");
            e.Handled = true;
            return;
        }

        // Shift+Tab → remove up to 4 leading spaces
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            RemoveLeadingSpaces();
            e.Handled = true;
            return;
        }

        // Enter → auto-indent
        if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.None)
        {
            AutoIndentNewLine();
            e.Handled = true;
            return;
        }
    }

    private void InsertAtCaret(string text)
    {
        var caretIndex = Editor.CaretIndex;
        Editor.Select(caretIndex, 0);
        Editor.SelectedText = text;
        Editor.CaretIndex = caretIndex + text.Length;
    }

    private void RemoveLeadingSpaces()
    {
        var lineIndex = Editor.GetLineIndexFromCharacterIndex(Editor.CaretIndex);
        if (lineIndex < 0) return;

        var lineStart = Editor.GetCharacterIndexFromLineIndex(lineIndex);
        var lineText = Editor.GetLineText(lineIndex);

        var spacesToRemove = 0;
        for (int i = 0; i < Math.Min(4, lineText.Length); i++)
        {
            if (lineText[i] == ' ') spacesToRemove++;
            else break;
        }

        if (spacesToRemove > 0)
        {
            var caretOffset = Editor.CaretIndex - lineStart;
            Editor.Select(lineStart, spacesToRemove);
            Editor.SelectedText = "";
            Editor.CaretIndex = lineStart + Math.Max(0, caretOffset - spacesToRemove);
        }
    }

    private void AutoIndentNewLine()
    {
        var lineIndex = Editor.GetLineIndexFromCharacterIndex(Editor.CaretIndex);
        if (lineIndex < 0) return;

        var lineText = Editor.GetLineText(lineIndex) ?? "";

        // Extract leading whitespace
        var indent = "";
        foreach (var ch in lineText)
        {
            if (ch == ' ' || ch == '\t') indent += ch;
            else break;
        }

        InsertAtCaret("\r\n" + indent);
    }

    #endregion

    #region Search / Replace

    public void ShowSearch()
    {
        SearchBar.Visibility = Visibility.Visible;
        SearchTextBox.Focus();
        SearchTextBox.SelectAll();
    }

    public void ShowReplace()
    {
        SearchBar.Visibility = Visibility.Visible;
        ReplaceRow.Visibility = Visibility.Visible;
        SearchTextBox.Focus();
        SearchTextBox.SelectAll();
    }

    public void HideSearch()
    {
        SearchBar.Visibility = Visibility.Collapsed;
        ReplaceRow.Visibility = Visibility.Collapsed;
        _matchPositions.Clear();
        _currentMatchIndex = -1;
        MatchCountLabel.Text = "";
        Editor.Focus();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateMatchPositions();
        if (string.IsNullOrEmpty(SearchTextBox.Text))
            MatchCountLabel.Text = "";
        else if (_matchPositions.Count > 0)
            NavigateToMatch(0);
        else
            MatchCountLabel.Text = "No results";
    }

    private void UpdateMatchPositions()
    {
        _matchPositions.Clear();
        _currentMatchIndex = -1;

        var query = SearchTextBox.Text;
        if (string.IsNullOrEmpty(query)) return;

        var text = Editor.Text ?? "";
        var pos = 0;
        while (true)
        {
            pos = text.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) break;
            _matchPositions.Add(pos);
            pos += query.Length;
        }
    }

    private void NavigateToMatch(int index)
    {
        if (_matchPositions.Count == 0) return;

        _currentMatchIndex = index;
        var pos = _matchPositions[index];
        var query = SearchTextBox.Text;

        Editor.Focus();
        Editor.Select(pos, query.Length);
        Editor.ScrollToLine(Editor.GetLineIndexFromCharacterIndex(pos));

        MatchCountLabel.Text = $"{index + 1} of {_matchPositions.Count}";
    }

    public void FindNext()
    {
        if (_matchPositions.Count == 0) return;
        var next = (_currentMatchIndex + 1) % _matchPositions.Count;
        NavigateToMatch(next);
    }

    public void FindPrevious()
    {
        if (_matchPositions.Count == 0) return;
        var prev = (_currentMatchIndex - 1 + _matchPositions.Count) % _matchPositions.Count;
        NavigateToMatch(prev);
    }

    private void ReplaceOne()
    {
        var query = SearchTextBox.Text;
        var replacement = ReplaceTextBox.Text ?? "";
        if (string.IsNullOrEmpty(query) || _matchPositions.Count == 0) return;

        // Check if current selection matches
        if (Editor.SelectedText.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            var selStart = Editor.SelectionStart;
            Editor.SelectedText = replacement;
            Editor.CaretIndex = selStart + replacement.Length;

            UpdateMatchPositions();
            if (_matchPositions.Count > 0)
            {
                // Find the next match at or after the replacement
                var nextIndex = _matchPositions.FindIndex(p => p >= selStart + replacement.Length);
                NavigateToMatch(nextIndex >= 0 ? nextIndex : 0);
            }
            else
            {
                MatchCountLabel.Text = "No results";
            }
        }
        else
        {
            FindNext();
        }
    }

    private void ReplaceAll()
    {
        var query = SearchTextBox.Text;
        var replacement = ReplaceTextBox.Text ?? "";
        if (string.IsNullOrEmpty(query)) return;

        var count = _matchPositions.Count;

        // Suppress re-entrant search updates during batch replace
        _suppressSearchUpdate = true;
        try
        {
            // Replace from end to start to preserve positions
            for (int i = _matchPositions.Count - 1; i >= 0; i--)
            {
                Editor.Select(_matchPositions[i], query.Length);
                Editor.SelectedText = replacement;
            }
        }
        finally { _suppressSearchUpdate = false; }

        UpdateMatchPositions();
        MatchCountLabel.Text = $"Replaced {count}";
    }

    #endregion

    #region Go to Line

    public void GoToLine()
    {
        var dialog = new Window
        {
            Title = "Go to Line",
            Width = 300,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            Background = (Brush)FindResource("SurfaceBrush"),
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow
        };

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var totalLines = Editor.LineCount;
        var label = new TextBlock
        {
            Text = $"Line number (1–{totalLines}):",
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(label, 0);

        var textBox = new TextBox
        {
            Background = (Brush)FindResource("BackgroundBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            CaretBrush = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Padding = new Thickness(8, 4, 8, 4),
            FontFamily = new FontFamily("Cascadia Code,Consolas,Courier New"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(textBox, 1);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetRow(buttonPanel, 2);

        int? result = null;
        var okButton = new Button
        {
            Content = "Go",
            Width = 80,
            Padding = new Thickness(0, 4, 0, 4),
            Margin = new Thickness(0, 0, 8, 0),
            Background = (Brush)FindResource("SurfaceLightBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            IsDefault = true
        };
        okButton.Click += (_, _) =>
        {
            if (int.TryParse(textBox.Text, out int line))
            {
                result = line;
                dialog.Close();
            }
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 80,
            Padding = new Thickness(0, 4, 0, 4),
            Background = (Brush)FindResource("SurfaceLightBrush"),
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            IsCancel = true
        };

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);

        grid.Children.Add(label);
        grid.Children.Add(textBox);
        grid.Children.Add(buttonPanel);
        dialog.Content = grid;
        dialog.ShowDialog();

        if (result.HasValue)
        {
            var lineNum = Math.Clamp(result.Value, 1, totalLines) - 1; // 0-based
            var charIndex = Editor.GetCharacterIndexFromLineIndex(lineNum);
            if (charIndex >= 0)
            {
                Editor.CaretIndex = charIndex;
                Editor.ScrollToLine(lineNum);
                Editor.Focus();
            }
        }
    }

    #endregion

    #region Event Handlers

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Sync DP ← TextBox
        if (Text != Editor.Text)
            Text = Editor.Text;

        UpdateLineNumbers();

        // Schedule syntax highlighting update (debounced)
        ScheduleHighlightUpdate();

        // Skip search update during batch operations (ReplaceAll)
        if (_suppressSearchUpdate) return;

        // Re-run search if active
        if (SearchBar.Visibility == Visibility.Visible && !string.IsNullOrEmpty(SearchTextBox.Text))
        {
            var prevCount = _matchPositions.Count;
            UpdateMatchPositions();
            if (_matchPositions.Count > 0 && _currentMatchIndex >= 0)
            {
                _currentMatchIndex = Math.Min(_currentMatchIndex, _matchPositions.Count - 1);
                MatchCountLabel.Text = $"{_currentMatchIndex + 1} of {_matchPositions.Count}";
            }
            else if (_matchPositions.Count == 0)
            {
                MatchCountLabel.Text = "No results";
            }
        }

        // IntelliSense hooks
        if (_completionProvider != null && !_suppressCompletionTrigger)
        {
            if (_completionActive)
            {
                FilterCompletion();
            }
            else
            {
                var text = Editor.Text ?? "";
                var caret = Editor.CaretIndex;
                var charTrigger = caret > 0 ? _completionProvider!.GetTriggerForCharacter(text[caret - 1]) : null;
                if (charTrigger.HasValue)
                {
                    TriggerCompletion(charTrigger.Value);
                }
                else
                {
                    ScheduleAutoTrigger();
                }
            }
        }
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        // Dismiss completion if caret moved outside the completion word
        if (_completionActive)
        {
            var caret = Editor.CaretIndex;
            var text = Editor.Text ?? "";
            if (caret < _completionAnchor || caret > text.Length)
            {
                DismissCompletion();
            }
            else
            {
                var segment = text[_completionAnchor..caret];
                // FIX: null-check _completionProvider instead of using null-forgiving operator
                if (segment.Length > 0 && (_completionProvider == null || !segment.All(c => _completionProvider.IsIdentifierChar(c))))
                    DismissCompletion();
            }
        }

        if (!_highlightingActive) return;

        var oldHighlight = _bracketHighlight;
        UpdateBracketHighlight();

        // Only re-render if bracket highlight changed
        if (oldHighlight != _bracketHighlight)
            SyncHighlightLayer();
    }

    private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
                FindPrevious();
            else
                FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideSearch();
            e.Handled = true;
        }
    }

    private void ReplaceTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ReplaceOne();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideSearch();
            e.Handled = true;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindPrevious();
    private void ToggleReplace_Click(object sender, RoutedEventArgs e)
    {
        ReplaceRow.Visibility = ReplaceRow.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
    private void CloseSearch_Click(object sender, RoutedEventArgs e) => HideSearch();
    private void ReplaceOne_Click(object sender, RoutedEventArgs e) => ReplaceOne();
    private void ReplaceAll_Click(object sender, RoutedEventArgs e) => ReplaceAll();
    private void CompletionList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptCompletion();

    #endregion

    #region IntelliSense

    private void TriggerCompletion(CompletionTrigger trigger)
    {
        if (_completionProvider == null) return;

        var text = Editor.Text ?? "";
        var caret = Editor.CaretIndex;

        // Don't trigger inside strings or comments
        if (IsInsideStringOrComment(caret))
        {
            DismissCompletion();
            return;
        }

        var items = _completionProvider.GetCompletions(text, caret, trigger, _cachedTokens);
        if (items.Count == 0)
        {
            DismissCompletion();
            return;
        }

        var (wordStart, _) = _completionProvider.GetWordAtCaret(text, caret);
        _completionAnchor = (trigger == CompletionTrigger.Dot || trigger == CompletionTrigger.Angle) ? caret : (wordStart >= 0 ? wordStart : caret);

        _allCompletionItems = items;
        CompletionList.ItemsSource = items;
        CompletionList.SelectedIndex = 0;

        PositionCompletionPopup();
        CompletionPopup.IsOpen = true;
        _completionActive = true;

        // Keep focus on the editor
        Editor.Focus();
    }

    private void DismissCompletion()
    {
        CompletionPopup.IsOpen = false;
        _completionActive = false;
        _allCompletionItems = [];
        _autoTriggerTimer?.Stop();
    }

    private void FilterCompletion()
    {
        var text = Editor.Text ?? "";
        var caret = Editor.CaretIndex;

        // Dismiss if caret moved before anchor
        if (caret < _completionAnchor)
        {
            DismissCompletion();
            return;
        }

        var prefix = text[_completionAnchor..caret];

        // Dismiss if prefix contains non-identifier character
        if (prefix.Length > 0 && _completionProvider != null && !prefix.All(c => _completionProvider.IsIdentifierChar(c)))
        {
            DismissCompletion();
            return;
        }

        List<CompletionItem> filtered;
        if (prefix.Length == 0)
        {
            filtered = _allCompletionItems;
        }
        else
        {
            filtered = _allCompletionItems
                .Where(item => item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (filtered.Count == 0)
        {
            DismissCompletion();
            return;
        }

        // If exactly one match and it equals the prefix, dismiss (user typed the full word)
        if (filtered.Count == 1 && filtered[0].Label.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            DismissCompletion();
            return;
        }

        CompletionList.ItemsSource = filtered;
        CompletionList.SelectedIndex = 0;
    }

    private void AcceptCompletion()
    {
        if (CompletionList.SelectedItem is not CompletionItem item)
        {
            DismissCompletion();
            return;
        }

        var caret = Editor.CaretIndex;
        var replaceLength = caret - _completionAnchor;

        // Expand snippet with current line indentation
        string insertText;
        int caretOffset;
        if (item.Kind == CompletionItemKind.Snippet)
            (insertText, caretOffset) = ExpandSnippet(item);
        else
            (insertText, caretOffset) = (item.InsertText, item.CaretOffset);

        // Suppress auto-trigger during insertion
        _suppressCompletionTrigger = true;
        try
        {
            Editor.Select(_completionAnchor, replaceLength);
            Editor.SelectedText = insertText;

            if (caretOffset >= 0)
                Editor.CaretIndex = _completionAnchor + caretOffset;
            else
                Editor.CaretIndex = _completionAnchor + insertText.Length;
        }
        finally
        {
            _suppressCompletionTrigger = false;
        }

        DismissCompletion();
    }

    private (string text, int caretOffset) ExpandSnippet(CompletionItem snippet)
    {
        // Get current line's leading whitespace
        var lineIndex = Editor.GetLineIndexFromCharacterIndex(_completionAnchor);
        var lineText = lineIndex >= 0 ? (Editor.GetLineText(lineIndex) ?? "") : "";
        var indent = "";
        foreach (var ch in lineText)
        {
            if (ch == ' ' || ch == '\t') indent += ch;
            else break;
        }

        var template = snippet.InsertText;
        var rawCaretOffset = snippet.CaretOffset;

        // Split by \r\n and add indent to each subsequent line
        var lines = template.Split("\r\n");
        if (lines.Length <= 1)
            return (template, rawCaretOffset);

        // Build expanded text and track caret offset
        var sb = new System.Text.StringBuilder();
        int expandedCaretOffset = -1;
        int currentPos = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                sb.Append("\r\n");
                sb.Append(indent);
                currentPos += 2 + indent.Length; // \r\n + indent
            }

            // Check if rawCaretOffset falls within this line
            int lineStartInTemplate = GetLineStartInTemplate(lines, i);
            int lineEndInTemplate = lineStartInTemplate + lines[i].Length;
            if (rawCaretOffset >= 0 && rawCaretOffset >= lineStartInTemplate && rawCaretOffset <= lineEndInTemplate)
            {
                expandedCaretOffset = currentPos + (rawCaretOffset - lineStartInTemplate);
            }

            sb.Append(lines[i]);
            currentPos += lines[i].Length;
        }

        return (sb.ToString(), expandedCaretOffset >= 0 ? expandedCaretOffset : rawCaretOffset);
    }

    private static int GetLineStartInTemplate(string[] lines, int lineIndex)
    {
        int pos = 0;
        for (int i = 0; i < lineIndex; i++)
            pos += lines[i].Length + 2; // +2 for \r\n
        return pos;
    }

    private void PositionCompletionPopup()
    {
        try
        {
            var rect = Editor.GetRectFromCharacterIndex(Editor.CaretIndex);
            CompletionPopup.HorizontalOffset = rect.Left;
            CompletionPopup.VerticalOffset = rect.Bottom + 2;

            // After layout, check if popup goes below viewport and flip above if needed
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (CompletionPopup.Child is FrameworkElement child && _editorScrollViewer != null)
                {
                    var popupHeight = child.ActualHeight;
                    var editorHeight = _editorScrollViewer.ViewportHeight;
                    if (rect.Bottom + popupHeight > editorHeight)
                    {
                        CompletionPopup.VerticalOffset = rect.Top - popupHeight - 2;
                    }
                }
            });
        }
        catch (Exception)
        {
            // GetRectFromCharacterIndex throws when caret position is not yet rendered
        }
    }

    private void ScheduleAutoTrigger()
    {
        if (_completionProvider == null) return;

        if (_autoTriggerTimer == null)
        {
            _autoTriggerTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _autoTriggerTimer.Tick += AutoTriggerTimer_Tick;
        }

        _autoTriggerTimer.Stop();
        _autoTriggerTimer.Start();
    }

    private void AutoTriggerTimer_Tick(object? sender, EventArgs e)
    {
        _autoTriggerTimer!.Stop();

        if (_completionProvider == null || _completionActive) return;

        var text = Editor.Text ?? "";
        var caret = Editor.CaretIndex;
        var (_, prefix) = _completionProvider.GetWordAtCaret(text, caret);

        if (prefix.Length >= 2)
            TriggerCompletion(CompletionTrigger.Typing);
    }

    private bool IsInsideStringOrComment(int position)
    {
        int lo = 0, hi = _cachedTokens.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var token = _cachedTokens[mid];
            if (token.Start + token.Length <= position)
                lo = mid + 1;
            else if (token.Start > position)
                hi = mid - 1;
            else // position is inside this token's range
                return token.Type is SyntaxTokenType.String or SyntaxTokenType.Comment;
        }
        return false;
    }

    #endregion
}
