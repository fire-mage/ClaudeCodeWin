using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow(MainViewModel viewModel, NotificationService notificationService,
        SettingsService settingsService, AppSettings settings)
    {
        InitializeComponent();
        DataContext = viewModel;

        _settingsService = settingsService;
        _settings = settings;

        notificationService.Initialize(this);

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

        // Auto-scroll when messages change or text streams in
        if (viewModel.Messages is INotifyCollectionChanged ncc)
        {
            ncc.CollectionChanged += (_, args) =>
            {
                ScrollToBottom();

                // Subscribe to text changes on new streaming messages
                if (args.NewItems is not null)
                {
                    foreach (MessageViewModel msg in args.NewItems)
                    {
                        msg.PropertyChanged += (_, pe) =>
                        {
                            if (pe.PropertyName == nameof(MessageViewModel.Text))
                                ScrollToBottom();
                        };
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

        // Escape = Cancel
        if (e.Key == Key.Escape && ViewModel.IsProcessing)
        {
            ViewModel.CancelCommand.Execute(null);
            e.Handled = true;
            return;
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

    private void ScrollToBottom()
    {
        Dispatcher.InvokeAsync(() =>
            ChatScrollViewer.ScrollToEnd(),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void MenuItem_Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MenuItem_About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "ClaudeCodeWin\nWPF wrapper for Claude Code CLI\n\nVersion 1.0",
            "About",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
