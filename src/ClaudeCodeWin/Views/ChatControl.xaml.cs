using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin.Views;

public partial class ChatControl : UserControl
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    private bool _isUserNearBottom = true;
    private readonly HashSet<MessageViewModel> _subscribedMessages = [];

    public static readonly DependencyProperty MessagesProperty =
        DependencyProperty.Register(nameof(Messages),
            typeof(ObservableCollection<MessageViewModel>),
            typeof(ChatControl),
            new PropertyMetadata(null, OnMessagesChanged));

    public static readonly DependencyProperty CommandSourceProperty =
        DependencyProperty.Register(nameof(CommandSource),
            typeof(object),
            typeof(ChatControl));

    public ObservableCollection<MessageViewModel>? Messages
    {
        get => (ObservableCollection<MessageViewModel>?)GetValue(MessagesProperty);
        set => SetValue(MessagesProperty, value);
    }

    /// <summary>
    /// Optional command source for main-chat-specific actions (NudgeCommand, SendTaskOutputCommand, AnswerQuestionCommand).
    /// When null, those buttons silently do nothing.
    /// </summary>
    public object? CommandSource
    {
        get => GetValue(CommandSourceProperty);
        set => SetValue(CommandSourceProperty, value);
    }

    /// <summary>
    /// Raised when a bookmark is toggled so the host can update UI (e.g. BookmarksButton visibility).
    /// </summary>
    public event Action? BookmarkToggled;

    public ChatControl()
    {
        InitializeComponent();
        ChatScrollViewer.ScrollChanged += ChatScrollViewer_ScrollChanged;
        Loaded += (_, _) => ResubscribeMessages();
        Unloaded += (_, _) =>
        {
            if (Messages is INotifyCollectionChanged ncc)
                ncc.CollectionChanged -= Messages_CollectionChanged;
            UnsubscribeAllMessages();
        };
    }

    private static void OnMessagesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (ChatControl)d;

        if (e.OldValue is INotifyCollectionChanged old)
            old.CollectionChanged -= ctrl.Messages_CollectionChanged;

        ctrl.UnsubscribeAllMessages();

        if (e.NewValue is INotifyCollectionChanged ncc)
            ncc.CollectionChanged += ctrl.Messages_CollectionChanged;

        if (e.NewValue is ObservableCollection<MessageViewModel> newMessages)
        {
            foreach (var msg in newMessages)
            {
                if (ctrl._subscribedMessages.Add(msg))
                {
                    msg.PropertyChanged += ctrl.OnMessagePropertyChanged;
                    if (msg.ToolUses is INotifyCollectionChanged toolNcc)
                        toolNcc.CollectionChanged += ctrl.OnToolUsesChanged;
                }
            }
        }

        ctrl._isUserNearBottom = true;
        ctrl.Dispatcher.BeginInvoke(() => ctrl.ChatScrollViewer.ScrollToEnd(),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        ScrollToBottomIfAtBottom();

        if (args.NewItems is not null)
        {
            foreach (MessageViewModel msg in args.NewItems)
            {
                if (_subscribedMessages.Add(msg))
                {
                    msg.PropertyChanged += OnMessagePropertyChanged;
                    if (msg.ToolUses is INotifyCollectionChanged toolNcc)
                        toolNcc.CollectionChanged += OnToolUsesChanged;
                }
            }
        }

        if (args.OldItems is not null)
        {
            foreach (MessageViewModel msg in args.OldItems)
            {
                if (_subscribedMessages.Remove(msg))
                {
                    msg.PropertyChanged -= OnMessagePropertyChanged;
                    if (msg.ToolUses is INotifyCollectionChanged toolNcc)
                        toolNcc.CollectionChanged -= OnToolUsesChanged;
                }
            }
        }

        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            UnsubscribeAllMessages();
            if (Messages is not null)
            {
                foreach (var msg in Messages)
                {
                    if (_subscribedMessages.Add(msg))
                    {
                        msg.PropertyChanged += OnMessagePropertyChanged;
                        if (msg.ToolUses is INotifyCollectionChanged toolNcc)
                            toolNcc.CollectionChanged += OnToolUsesChanged;
                    }
                }
            }
        }
    }

    private void OnMessagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs pe)
    {
        if (pe.PropertyName is nameof(MessageViewModel.Text)
            or nameof(MessageViewModel.IsThinking)
            or nameof(MessageViewModel.HasToolUses))
            ScrollToBottomIfAtBottom();
    }

    private void OnToolUsesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScrollToBottomIfAtBottom();

    private void UnsubscribeAllMessages()
    {
        foreach (var msg in _subscribedMessages)
        {
            msg.PropertyChanged -= OnMessagePropertyChanged;
            if (msg.ToolUses is INotifyCollectionChanged toolNcc)
                toolNcc.CollectionChanged -= OnToolUsesChanged;
        }
        _subscribedMessages.Clear();
    }

    /// <summary>Re-subscribe after Loaded (handles Unloaded/Loaded cycles in TabControl scenarios).</summary>
    private void ResubscribeMessages()
    {
        if (Messages is null) return;

        if (Messages is INotifyCollectionChanged ncc)
        {
            ncc.CollectionChanged -= Messages_CollectionChanged;
            ncc.CollectionChanged += Messages_CollectionChanged;
        }

        foreach (var msg in Messages)
        {
            if (_subscribedMessages.Add(msg))
            {
                msg.PropertyChanged += OnMessagePropertyChanged;
                if (msg.ToolUses is INotifyCollectionChanged toolNcc)
                    toolNcc.CollectionChanged += OnToolUsesChanged;
            }
        }
    }

    // ── Scroll helpers ──────────────────────────────────────────

    private bool IsNearBottom()
    {
        var sv = ChatScrollViewer;
        return sv.VerticalOffset >= sv.ScrollableHeight - 50;
    }

    private void ScrollToBottomIfAtBottom()
    {
        if (_isUserNearBottom)
        {
            Dispatcher.InvokeAsync(() =>
                ChatScrollViewer.ScrollToEnd(),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void ChatScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
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

    /// <summary>Scroll to a specific message in the list.</summary>
    public void ScrollToMessage(MessageViewModel msg)
    {
        var container = MessagesControl.ItemContainerGenerator.ContainerFromItem(msg) as FrameworkElement;
        if (container is not null)
            container.BringIntoView();
    }

    /// <summary>Force scroll to bottom (e.g. on tab switch).</summary>
    public void ScrollToEnd()
    {
        _isUserNearBottom = true;
        Dispatcher.InvokeAsync(() =>
            ChatScrollViewer.ScrollToEnd(),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ── Message event handlers ──────────────────────────────────

    private void CopyAllMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string text && !string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    private void ToggleBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            var contextMenu = menuItem.Parent as ContextMenu;
            var textBox = contextMenu?.PlacementTarget as TextBox;
            if (textBox?.DataContext is MessageViewModel msg)
            {
                msg.IsBookmarked = !msg.IsBookmarked;
                menuItem.Header = msg.IsBookmarked ? "Remove Bookmark" : "Bookmark";
                BookmarkToggled?.Invoke();
            }
        }
    }

    private void ThinkingToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MessageViewModel vm)
            vm.IsThinkingExpanded = !vm.IsThinkingExpanded;
    }

    private void AttachmentImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image img && img.Tag is string filePath)
        {
            try
            {
                var ext = Path.GetExtension(filePath);
                if (!ImageExtensions.Contains(ext) || !File.Exists(filePath))
                    return;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 400;
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

    private void InlineImage_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is string filePath && File.Exists(filePath))
            ShowImagePreviewWindow(filePath);
    }

    private void AttachmentPreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Models.FileAttachment att
            && att.IsImage && File.Exists(att.FilePath))
            ShowImagePreviewWindow(att.FilePath, att.FileName);
    }

    private void ShowImagePreviewWindow(string filePath, string? title = null)
    {
        var owner = Window.GetWindow(this);
        if (owner is not null)
            Infrastructure.ImagePreviewHelper.ShowPreviewWindow(owner, filePath, title);
    }
}
