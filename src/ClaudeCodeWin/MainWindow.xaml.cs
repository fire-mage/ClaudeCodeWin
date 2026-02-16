using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly FileIndexService _fileIndexService;
    private readonly ChatHistoryService _chatHistoryService;
    private readonly ProjectRegistryService _projectRegistry;
    private CancellationTokenSource? _autocompleteCts;

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow(MainViewModel viewModel, NotificationService notificationService,
        SettingsService settingsService, AppSettings settings, FileIndexService fileIndexService,
        ChatHistoryService chatHistoryService, ProjectRegistryService projectRegistry)
    {
        InitializeComponent();
        DataContext = viewModel;

        _settingsService = settingsService;
        _settings = settings;
        _fileIndexService = fileIndexService;
        _chatHistoryService = chatHistoryService;
        _projectRegistry = projectRegistry;

        notificationService.Initialize(this);

        InputTextBox.TextChanged += InputTextBox_TextChanged;
        AutocompleteList.MouseDoubleClick += AutocompleteList_MouseDoubleClick;

        // Rebuild Recent Projects submenu whenever the collection changes
        viewModel.RecentFolders.CollectionChanged += (_, _) => RebuildRecentProjectsMenu();
        RebuildRecentProjectsMenu();

        // Initialize Beta Updates checkbox from settings
        BetaUpdatesMenuItem.IsChecked = settings.UpdateChannel == "beta";

        // Set window icon from embedded resource
        try
        {
            var iconUri = new Uri("pack://application:,,,/app.ico", UriKind.Absolute);
            var sri = Application.GetResourceStream(iconUri);
            if (sri != null)
            {
                var decoder = BitmapDecoder.Create(sri.Stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Icon = decoder.Frames[0];
                sri.Stream.Dispose();
            }
        }
        catch
        {
            // Icon not found — not critical, continue without it
        }

        // Track scroll position for "scroll to bottom" button
        ChatScrollViewer.ScrollChanged += ChatScrollViewer_ScrollChanged;

        // Auto-scroll when messages change or text streams in (only if user is at bottom)
        if (viewModel.Messages is INotifyCollectionChanged ncc)
        {
            ncc.CollectionChanged += (_, args) =>
            {
                ScrollToBottomIfAtBottom();

                // Subscribe to changes on new messages
                if (args.NewItems is not null)
                {
                    foreach (MessageViewModel msg in args.NewItems)
                    {
                        // Text streaming
                        msg.PropertyChanged += (_, pe) =>
                        {
                            if (pe.PropertyName is nameof(MessageViewModel.Text)
                                or nameof(MessageViewModel.IsThinking)
                                or nameof(MessageViewModel.HasToolUses))
                                ScrollToBottomIfAtBottom();
                        };

                        // Tool uses being added (expands the bubble)
                        if (msg.ToolUses is INotifyCollectionChanged toolNcc)
                        {
                            toolNcc.CollectionChanged += (_, _) => ScrollToBottomIfAtBottom();
                        }
                    }
                }
            };
        }

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue)
        {
            Width = _settings.WindowWidth.Value;
            Height = _settings.WindowHeight.Value;

            if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue)
            {
                // Validate position is on-screen
                var left = _settings.WindowLeft.Value;
                var top = _settings.WindowTop.Value;

                if (IsPositionOnScreen(left, top, Width, Height))
                {
                    Left = left;
                    Top = top;
                    WindowStartupLocation = WindowStartupLocation.Manual;
                }
            }

            if (_settings.WindowState is 2)
                WindowState = WindowState.Maximized;
        }
        else
        {
            // First launch — maximized
            WindowState = WindowState.Maximized;
        }

        InputTextBox.Focus();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Save window state before closing
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }
        else if (WindowState == WindowState.Maximized)
        {
            // Save the restored bounds so we have a valid Normal size
            _settings.WindowWidth = RestoreBounds.Width;
            _settings.WindowHeight = RestoreBounds.Height;
            _settings.WindowLeft = RestoreBounds.Left;
            _settings.WindowTop = RestoreBounds.Top;
        }

        _settings.WindowState = WindowState == WindowState.Maximized ? 2 : 0;
        _settingsService.Save(_settings);
    }

    private static bool IsPositionOnScreen(double left, double top, double width, double height)
    {
        // Check if the window is at least partially within the virtual screen bounds
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        return left + width > virtualLeft && left < virtualRight &&
               top + height > virtualTop && top < virtualBottom;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Autocomplete navigation
        if (AutocompletePopup.IsOpen)
        {
            if (e.Key == Key.Down)
            {
                AutocompleteList.SelectedIndex =
                    Math.Min(AutocompleteList.SelectedIndex + 1, AutocompleteList.Items.Count - 1);
                AutocompleteList.ScrollIntoView(AutocompleteList.SelectedItem);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Up)
            {
                AutocompleteList.SelectedIndex = Math.Max(AutocompleteList.SelectedIndex - 1, 0);
                AutocompleteList.ScrollIntoView(AutocompleteList.SelectedItem);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Tab || e.Key == Key.Enter)
            {
                InsertAutocomplete();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                AutocompletePopup.IsOpen = false;
                e.Handled = true;
                return;
            }
        }

        // Enter or Ctrl+Enter = Send (when InputTextBox is focused)
        if (e.Key == Key.Enter && InputTextBox.IsFocused)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                // Shift+Enter = insert newline
                var caretIndex = InputTextBox.CaretIndex;
                InputTextBox.Text = InputTextBox.Text.Insert(caretIndex, "\r\n");
                InputTextBox.CaretIndex = caretIndex + 2;
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Plain Enter or Ctrl+Enter = send
                if (ViewModel.SendCommand.CanExecute(null))
                    ViewModel.SendCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // Escape = LIFO: pop queue → input, then cancel Claude
        if (e.Key == Key.Escape)
        {
            if (ViewModel.HandleEscape())
            {
                e.Handled = true;
                return;
            }
        }

        // Ctrl+N = New session
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ViewModel.NewSessionCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Ctrl+V = Paste (handle screenshots)
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (Clipboard.ContainsImage())
            {
                HandleClipboardImage();
                e.Handled = true;
                return;
            }
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var file in files)
            {
                ViewModel.AddAttachment(new FileAttachment
                {
                    FilePath = file,
                    FileName = Path.GetFileName(file),
                    IsScreenshot = false
                });
            }
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void HandleClipboardImage()
    {
        var image = Clipboard.GetImage();
        if (image is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "ClaudeCodeWin");
        Directory.CreateDirectory(tempDir);
        var fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var filePath = Path.Combine(tempDir, fileName);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(filePath))
        {
            encoder.Save(stream);
        }

        ViewModel.AddAttachment(new FileAttachment
        {
            FilePath = filePath,
            FileName = fileName,
            IsScreenshot = true
        });
    }

    private async void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _autocompleteCts?.Cancel();
        _autocompleteCts = new CancellationTokenSource();
        var token = _autocompleteCts.Token;

        try
        {
            await Task.Delay(150, token);
        }
        catch (TaskCanceledException) { return; }

        var text = InputTextBox.Text;
        var caret = InputTextBox.CaretIndex;
        var word = ExtractCurrentWord(text, caret);

        // For path queries like "src/com", check length of the part after last "/"
        var searchPart = word.Contains('/') ? word[(word.LastIndexOf('/') + 1)..] : word;
        if (searchPart.Length < 3 && !word.EndsWith('/'))
        {
            AutocompletePopup.IsOpen = false;
            return;
        }

        var results = _fileIndexService.Search(word);
        if (results.Count == 0)
        {
            AutocompletePopup.IsOpen = false;
            return;
        }

        AutocompleteList.ItemsSource = results;
        AutocompleteList.SelectedIndex = 0;

        // Position popup near the caret
        PositionAutocompletePopup(caret);
        AutocompletePopup.IsOpen = true;
    }

    private void PositionAutocompletePopup(int caretIndex)
    {
        try
        {
            var rect = InputTextBox.GetRectFromCharacterIndex(caretIndex);
            if (rect.IsEmpty) return;

            // Position popup at the caret X, above the caret line (like Intellisense going upward)
            AutocompletePopup.HorizontalOffset = rect.Left;
            // Negative offset to show above: go up from caret position
            // With Placement="Relative", offset is from top-left of TextBox
            // We want to show ABOVE the current line, so subtract estimated popup height
            AutocompletePopup.VerticalOffset = rect.Top;
            AutocompletePopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;

            // After rendering, adjust to show above
            AutocompletePopup.Dispatcher.InvokeAsync(() =>
            {
                if (AutocompletePopup.Child is FrameworkElement child && child.ActualHeight > 0)
                {
                    AutocompletePopup.VerticalOffset = rect.Top - child.ActualHeight - 4;
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch
        {
            AutocompletePopup.HorizontalOffset = 0;
            AutocompletePopup.VerticalOffset = 0;
        }
    }

    private static string ExtractCurrentWord(string text, int caretIndex)
    {
        if (caretIndex <= 0 || caretIndex > text.Length) return "";
        var start = caretIndex - 1;
        while (start >= 0 && !char.IsWhiteSpace(text[start]))
            start--;
        start++;
        return text[start..caretIndex];
    }

    private void InsertAutocomplete()
    {
        if (AutocompleteList.SelectedItem is not string selected) return;

        var text = InputTextBox.Text;
        var caret = InputTextBox.CaretIndex;

        var start = caret - 1;
        while (start >= 0 && !char.IsWhiteSpace(text[start]))
            start--;
        start++;

        var newText = text[..start] + selected + text[caret..];
        InputTextBox.Text = newText;
        InputTextBox.CaretIndex = start + selected.Length;
        AutocompletePopup.IsOpen = false;
    }

    private void AutocompleteList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        InsertAutocomplete();
    }

    private bool _isUserNearBottom = true;

    private bool IsNearBottom()
    {
        var sv = ChatScrollViewer;
        return sv.VerticalOffset >= sv.ScrollableHeight - 50;
    }

    private void ScrollToBottomIfAtBottom()
    {
        if (_isUserNearBottom)
        {
            // Use Loaded priority so WPF finishes layout/measure before we scroll
            Dispatcher.InvokeAsync(() =>
                ChatScrollViewer.ScrollToEnd(),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void ChatScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Only update user intent when the user actually scrolled (not when content grew)
        if (e.ExtentHeightChange == 0)
        {
            _isUserNearBottom = IsNearBottom();
        }
        ScrollToBottomButton.Visibility = IsNearBottom()
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ScrollToBottomButton_Click(object sender, RoutedEventArgs e)
    {
        ChatScrollViewer.ScrollToEnd();
    }

    private void CopyAllMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string text && !string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    private void MemoryIndicator_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is not null)
        {
            element.ContextMenu.PlacementTarget = element;
            element.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void ModelIndicator_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element
            && element.ContextMenu is not null
            && DataContext is ClaudeCodeWin.ViewModels.MainViewModel vm
            && vm.CanSwitchToOpus)
        {
            element.ContextMenu.PlacementTarget = element;
            element.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void AttachmentImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Image img && img.Tag is string filePath)
        {
            try
            {
                var ext = Path.GetExtension(filePath);
                var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

                if (!imageExts.Contains(ext) || !File.Exists(filePath))
                    return;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 400; // thumbnail
                bitmap.EndInit();
                bitmap.Freeze();

                img.Source = bitmap;
                img.Visibility = Visibility.Visible;
            }
            catch
            {
                // Can't load image — keep collapsed
            }
        }
    }

    private void AttachmentPreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Models.FileAttachment att)
        {
            if (att.IsImage && File.Exists(att.FilePath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(att.FilePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();

                var image = new System.Windows.Controls.Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly
                };

                var previewWindow = new Window
                {
                    Title = att.FileName,
                    Width = Math.Min(bitmap.PixelWidth + 40, 1200),
                    Height = Math.Min(bitmap.PixelHeight + 60, 800),
                    MinWidth = 300,
                    MinHeight = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    Background = (Brush)FindResource("BackgroundBrush"),
                    Content = new ScrollViewer
                    {
                        Content = image,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Padding = new Thickness(8)
                    }
                };

                previewWindow.ShowDialog();
            }
        }
    }

    private void RebuildRecentProjectsMenu()
    {
        RecentProjectsMenu.Items.Clear();

        if (ViewModel.RecentFolders.Count == 0)
        {
            RecentProjectsMenu.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
            return;
        }

        foreach (var folder in ViewModel.RecentFolders)
        {
            var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textBlock = new TextBlock
            {
                Text = $"{folderName}  ({folder})",
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 500
            };
            Grid.SetColumn(textBlock, 0);
            grid.Children.Add(textBlock);

            var folderForNotes = folder;
            var hasNotes = !string.IsNullOrEmpty(_projectRegistry.GetNotes(folder));
            var notesBtn = new Button
            {
                Content = hasNotes ? "\U0001F4DD" : "\U0001F4C4",
                FontSize = 10,
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = hasNotes
                    ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                    : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                ToolTip = "Edit project notes"
            };
            notesBtn.Click += (_, e) =>
            {
                e.Handled = true;
                var dlg = new ProjectNotesDialog(_projectRegistry, folderForNotes) { Owner = this };
                if (dlg.ShowDialog() == true)
                    RebuildRecentProjectsMenu();
            };
            Grid.SetColumn(notesBtn, 1);
            grid.Children.Add(notesBtn);

            var removeBtn = new Button
            {
                Content = "\u2715",
                FontSize = 10,
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                ToolTip = "Remove from recent"
            };
            var folderForRemove = folder;
            removeBtn.Click += (_, e) =>
            {
                e.Handled = true;
                var result = MessageBox.Show($"Remove \"{folderForRemove}\" from recent projects?",
                    "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                    ViewModel.RemoveRecentFolderCommand.Execute(folderForRemove);
            };
            Grid.SetColumn(removeBtn, 2);
            grid.Children.Add(removeBtn);

            var menuItem = new MenuItem { Header = grid };
            var folderForOpen = folder;
            menuItem.Click += (_, _) => ViewModel.OpenRecentFolderCommand.Execute(folderForOpen);
            RecentProjectsMenu.Items.Add(menuItem);
        }
    }

    private void MenuItem_Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MenuItem_ChatHistory_Click(object sender, RoutedEventArgs e)
    {
        var historyWindow = new ChatHistoryWindow(_chatHistoryService)
        {
            Owner = this
        };

        if (historyWindow.ShowDialog() == true && historyWindow.SelectedEntry is not null)
        {
            ViewModel.LoadChatFromHistory(historyWindow.SelectedEntry);
        }
    }

    private void QuickPrompt_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is not null)
        {
            element.ContextMenu.PlacementTarget = element;
            element.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void QuickPromptItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string prompt)
        {
            if (ViewModel.QuickPromptCommand.CanExecute(prompt))
                ViewModel.QuickPromptCommand.Execute(prompt);
        }
    }

    private void ToggleBookmark_Click(object sender, RoutedEventArgs e)
    {
        // Walk up the visual tree from the context menu to find the MessageViewModel
        if (sender is MenuItem menuItem)
        {
            var contextMenu = menuItem.Parent as ContextMenu;
            var textBox = contextMenu?.PlacementTarget as System.Windows.Controls.TextBox;
            if (textBox?.DataContext is MessageViewModel msg)
            {
                msg.IsBookmarked = !msg.IsBookmarked;
                menuItem.Header = msg.IsBookmarked ? "Remove Bookmark" : "Bookmark";
                UpdateBookmarksButton();
            }
        }
    }

    private void UpdateBookmarksButton()
    {
        var hasBookmarks = ViewModel.Messages.Any(m => m.IsBookmarked);
        BookmarksButton.Visibility = hasBookmarks ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BookmarksButton_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var bookmarked = ViewModel.Messages.Where(m => m.IsBookmarked).ToList();
        if (bookmarked.Count == 0) return;

        BookmarksContextMenu.Items.Clear();
        foreach (var msg in bookmarked)
        {
            var preview = msg.Text.Length > 60 ? msg.Text[..60] + "..." : msg.Text;
            preview = preview.Replace("\r", "").Replace("\n", " ");
            var item = new MenuItem
            {
                Header = $"{msg.Timestamp:HH:mm} | {preview}",
                Foreground = (Brush)FindResource("TextBrush")
            };
            var target = msg;
            item.Click += (_, _) => ScrollToMessage(target);
            BookmarksContextMenu.Items.Add(item);
        }

        BookmarksContextMenu.PlacementTarget = (UIElement)sender;
        BookmarksContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void ScrollToMessage(MessageViewModel msg)
    {
        // Find the container for this message and scroll to it
        var container = MessagesControl.ItemContainerGenerator.ContainerFromItem(msg) as FrameworkElement;
        if (container is not null)
            container.BringIntoView();
    }

    private void MenuItem_Servers_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ServerRegistryWindow(_settings, _settingsService) { Owner = this };
        dlg.ShowDialog();
    }

    private void MenuItem_BetaUpdates_Click(object sender, RoutedEventArgs e)
    {
        var channel = BetaUpdatesMenuItem.IsChecked ? "beta" : "stable";
        _settings.UpdateChannel = channel;
        _settingsService.Save(_settings);
        ViewModel.SetUpdateChannel(channel);
    }

    private void MenuItem_About_Click(object sender, RoutedEventArgs e)
    {
        var infoVersion = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        var parts = infoVersion.Split('+');
        var version = parts[0];
        var buildHash = parts.Length > 1 ? parts[1][..Math.Min(7, parts[1].Length)] : "";

        var exePath = Environment.ProcessPath ?? "";
        var buildDate = !string.IsNullOrEmpty(exePath) && File.Exists(exePath)
            ? File.GetLastWriteTime(exePath).ToString("yyyy-MM-dd")
            : "unknown";

        const string email = "claudecodewin.support@main.fish";

        var aboutWindow = new Window
        {
            Title = "About ClaudeCodeWin",
            Width = 420,
            Height = 280,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("BackgroundBrush"),
        };

        var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

        stack.Children.Add(new TextBlock
        {
            Text = $"ClaudeCodeWin v{version}",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("PrimaryBrush")
        });

        stack.Children.Add(new TextBlock
        {
            Text = "WPF GUI for Claude Code CLI",
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (Brush)FindResource("TextSecondaryBrush")
        });

        var buildText = $"Built: {buildDate}";
        if (!string.IsNullOrEmpty(buildHash))
            buildText += $"  |  Build: {buildHash}";

        stack.Children.Add(new TextBlock
        {
            Text = buildText,
            Margin = new Thickness(0, 16, 0, 0),
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush")
        });

        // Email row with subtle copy button
        var emailPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 16, 0, 0)
        };

        emailPanel.Children.Add(new TextBlock
        {
            Text = $"Support: {email}",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        });

        var copyButton = new Button
        {
            Content = "Copy",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 10,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        copyButton.Click += (_, _) =>
        {
            Clipboard.SetText(email);
            copyButton.Content = "Copied!";
        };
        emailPanel.Children.Add(copyButton);
        stack.Children.Add(emailPanel);

        // Close button — prominent, primary style
        var closeButton = new Button
        {
            Content = "Close",
            Margin = new Thickness(0, 24, 0, 0),
            Padding = new Thickness(32, 8, 32, 8),
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        closeButton.Click += (_, _) => aboutWindow.Close();
        stack.Children.Add(closeButton);

        aboutWindow.Content = stack;
        aboutWindow.ShowDialog();
    }
}
