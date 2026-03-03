using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Views;

public partial class MessageComposerControl : UserControl
{
    private TextBox? _focusedTextBox;
    private TextBox? _lastActiveTextBox;

    public static readonly DependencyProperty ComposerBlocksProperty =
        DependencyProperty.Register(
            nameof(ComposerBlocks),
            typeof(ObservableCollection<ComposerBlock>),
            typeof(MessageComposerControl),
            new PropertyMetadata(null, OnBlocksChanged));

    public ObservableCollection<ComposerBlock>? ComposerBlocks
    {
        get => (ObservableCollection<ComposerBlock>?)GetValue(ComposerBlocksProperty);
        set => SetValue(ComposerBlocksProperty, value);
    }

    /// <summary>Raised when any text block's content changes (for autocomplete).</summary>
    public event Action<TextBox>? ActiveTextChanged;

    /// <summary>Raised when block structure changes (image added/removed).</summary>
    public event Action? BlocksChanged;

    /// <summary>Raised when user presses Enter to send.</summary>
    public event Action? SendRequested;

    /// <summary>Raised when user presses Escape. Return true if handled.</summary>
    public event Func<bool>? EscapePressed;

    /// <summary>Raised when user presses Up on first line of first block with empty composer.</summary>
    public event Action? RecallRequested;

    public MessageComposerControl()
    {
        InitializeComponent();
    }

    private static void OnBlocksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (MessageComposerControl)d;
        ctrl._focusedTextBox = null;
        ctrl._lastActiveTextBox = null;

        if (e.OldValue is ObservableCollection<ComposerBlock> oldColl)
            oldColl.CollectionChanged -= ctrl.Blocks_CollectionChanged;

        var newColl = e.NewValue as ObservableCollection<ComposerBlock>;
        ctrl.BlocksControl.ItemsSource = newColl;

        if (newColl is not null)
            newColl.CollectionChanged += ctrl.Blocks_CollectionChanged;
    }

    private void Blocks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
        {
            _focusedTextBox = null;
            _lastActiveTextBox = null;
        }
    }

    /// <summary>Whether any child TextBox has keyboard focus.</summary>
    public bool HasTextFocus => _focusedTextBox is not null && _focusedTextBox.IsFocused;

    /// <summary>Get the currently focused TextBox (or null).</summary>
    public TextBox? FocusedTextBox => _focusedTextBox;

    /// <summary>Last TextBox that had focus (survives focus loss to autocomplete list).</summary>
    public TextBox? LastActiveTextBox => _lastActiveTextBox;

    /// <summary>Focus the first text block in the composer.</summary>
    public void FocusFirst()
    {
        Dispatcher.InvokeAsync(() =>
        {
            var tb = FindTextBoxForBlock(0);
            tb?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Focus the first text block and place caret at end.</summary>
    public void FocusEnd()
    {
        Dispatcher.InvokeAsync(() =>
        {
            var blocks = ComposerBlocks;
            if (blocks is null || blocks.Count == 0) return;
            // Find last text block
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                if (blocks[i] is TextComposerBlock)
                {
                    var tb = FindTextBoxForBlock(i);
                    if (tb is not null)
                    {
                        tb.Focus();
                        tb.CaretIndex = tb.Text.Length;
                    }
                    return;
                }
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ─── Paste image inline ───

    /// <summary>Paste a clipboard image at the current cursor position, splitting the text block.</summary>
    public void PasteImage(BitmapSource image)
    {
        var blocks = ComposerBlocks;
        if (blocks is null) return;

        // Save image to temp file
        var tempDir = Path.Combine(Path.GetTempPath(), "ClaudeCodeWin");
        Directory.CreateDirectory(tempDir);
        var fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Random.Shared.Next(1000):D3}.png";
        var filePath = Path.Combine(tempDir, fileName);

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(filePath);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PasteImage failed: {ex.Message}");
            return;
        }

        var attachment = new FileAttachment
        {
            FilePath = filePath,
            FileName = fileName,
            IsScreenshot = true
        };

        var imageBlock = new ImageComposerBlock(attachment);

        // Find the focused text block and its caret position
        int blockIndex = GetFocusedBlockIndex();
        if (blockIndex < 0 || blocks[blockIndex] is not TextComposerBlock textBlock)
        {
            // No text block focused — append at end
            blocks.Add(imageBlock);
            blocks.Add(new TextComposerBlock());
            BlocksChanged?.Invoke();
            FocusEnd();
            return;
        }

        var tb = _focusedTextBox;
        int caret = tb?.CaretIndex ?? textBlock.Text.Length;
        string leftText = textBlock.Text[..caret];
        string rightText = textBlock.Text[caret..];

        // Update current block with left part
        textBlock.Text = leftText;

        // Insert image and right-part text block after current
        int insertAt = blockIndex + 1;
        blocks.Insert(insertAt, imageBlock);
        blocks.Insert(insertAt + 1, new TextComposerBlock(rightText));

        BlocksChanged?.Invoke();

        // Focus the new right text block at position 0
        Dispatcher.InvokeAsync(() =>
        {
            var newTb = FindTextBoxForBlock(insertAt + 1);
            if (newTb is not null)
            {
                newTb.Focus();
                newTb.CaretIndex = 0;
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ─── Remove image and merge text blocks ───

    private void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ImageComposerBlock imageBlock) return;
        RemoveImageBlock(imageBlock);
    }

    private void RemoveImageBlock(ImageComposerBlock imageBlock)
    {
        var blocks = ComposerBlocks;
        if (blocks is null) return;

        int idx = blocks.IndexOf(imageBlock);
        if (idx < 0) return;

        blocks.RemoveAt(idx);

        // Merge adjacent text blocks
        if (idx > 0 && idx < blocks.Count
            && blocks[idx - 1] is TextComposerBlock before
            && blocks[idx] is TextComposerBlock after)
        {
            int mergePoint = before.Text.Length;
            before.Text += after.Text;
            blocks.RemoveAt(idx);

            // Focus the merged block at merge point
            Dispatcher.InvokeAsync(() =>
            {
                var tb = FindTextBoxForBlock(idx - 1);
                if (tb is not null)
                {
                    tb.Focus();
                    tb.CaretIndex = mergePoint;
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Ensure there's always at least one text block
        if (blocks.Count == 0)
            blocks.Add(new TextComposerBlock());

        BlocksChanged?.Invoke();
    }

    // ─── Text block events ───

    private void TextBlock_GotFocus(object sender, RoutedEventArgs e)
    {
        _focusedTextBox = sender as TextBox;
        _lastActiveTextBox = _focusedTextBox;
    }

    private void TextBlock_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_focusedTextBox == sender)
            _focusedTextBox = null;
    }

    private void TextBlock_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
            ActiveTextChanged?.Invoke(tb);
    }

    private void TextBlock_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var blocks = ComposerBlocks;
        if (blocks is null) return;

        int blockIdx = GetBlockIndexForTextBox(tb);

        // Enter / Ctrl+Enter = Send
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                // Shift+Enter = newline
                int caret = tb.CaretIndex;
                tb.Text = tb.Text.Insert(caret, "\r\n");
                tb.CaretIndex = caret + 2;
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control)
            {
                SendRequested?.Invoke();
                e.Handled = true;
                return;
            }
        }

        // Escape
        if (e.Key == Key.Escape)
        {
            if (EscapePressed?.Invoke() == true)
                e.Handled = true;
            return;
        }

        // Up arrow at first line
        if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.None)
        {
            int caretLine = tb.GetLineIndexFromCharacterIndex(tb.CaretIndex);
            if (caretLine <= 0)
            {
                // Try to move to previous text block
                var prevTb = FindPreviousTextBox(blockIdx);
                if (prevTb is not null)
                {
                    prevTb.Focus();
                    prevTb.CaretIndex = prevTb.Text.Length;
                    e.Handled = true;
                    return;
                }
                // First block, empty = recall
                if (IsComposerEmpty())
                {
                    RecallRequested?.Invoke();
                    e.Handled = true;
                    return;
                }
                // First block, has text = go to start
                tb.CaretIndex = 0;
                e.Handled = true;
                return;
            }
        }

        // Down arrow at last line
        if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.None)
        {
            int caretLine = tb.GetLineIndexFromCharacterIndex(tb.CaretIndex);
            if (caretLine >= tb.LineCount - 1)
            {
                var nextTb = FindNextTextBox(blockIdx);
                if (nextTb is not null)
                {
                    nextTb.Focus();
                    nextTb.CaretIndex = 0;
                    e.Handled = true;
                    return;
                }
                // Last block = go to end
                tb.CaretIndex = tb.Text.Length;
                e.Handled = true;
                return;
            }
        }

        // Backspace at position 0 — delete previous image
        if (e.Key == Key.Back && tb.CaretIndex == 0 && tb.SelectionLength == 0)
        {
            if (blockIdx > 0 && blocks[blockIdx - 1] is ImageComposerBlock prevImg)
            {
                RemoveImageBlock(prevImg);
                e.Handled = true;
                return;
            }
        }

        // Delete at end of text — delete next image
        if (e.Key == Key.Delete && tb.CaretIndex == tb.Text.Length && tb.SelectionLength == 0)
        {
            if (blockIdx >= 0 && blockIdx + 1 < blocks.Count && blocks[blockIdx + 1] is ImageComposerBlock nextImg)
            {
                RemoveImageBlock(nextImg);
                e.Handled = true;
                return;
            }
        }
    }

    // ─── Helpers ───

    private bool IsComposerEmpty()
    {
        var blocks = ComposerBlocks;
        return blocks is not null
            && blocks.Count == 1
            && blocks[0] is TextComposerBlock tb
            && string.IsNullOrWhiteSpace(tb.Text);
    }

    private int GetFocusedBlockIndex()
    {
        if (_focusedTextBox is null) return -1;
        return GetBlockIndexForTextBox(_focusedTextBox);
    }

    private int GetBlockIndexForTextBox(TextBox tb)
    {
        var blocks = ComposerBlocks;
        if (blocks is null) return -1;

        // The TextBox's Tag is bound to the ComposerBlock
        if (tb.Tag is ComposerBlock block)
            return blocks.IndexOf(block);
        return -1;
    }

    private TextBox? FindTextBoxForBlock(int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= BlocksControl.Items.Count) return null;
        var container = BlocksControl.ItemContainerGenerator.ContainerFromIndex(blockIndex) as ContentPresenter;
        if (container is null) return null;
        return FindChild<TextBox>(container);
    }

    private TextBox? FindPreviousTextBox(int currentBlockIdx)
    {
        var blocks = ComposerBlocks;
        if (blocks is null) return null;
        for (int i = currentBlockIdx - 1; i >= 0; i--)
        {
            if (blocks[i] is TextComposerBlock)
                return FindTextBoxForBlock(i);
        }
        return null;
    }

    private TextBox? FindNextTextBox(int currentBlockIdx)
    {
        var blocks = ComposerBlocks;
        if (blocks is null) return null;
        for (int i = currentBlockIdx + 1; i < blocks.Count; i++)
        {
            if (blocks[i] is TextComposerBlock)
                return FindTextBoxForBlock(i);
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindChild<T>(child);
            if (result is not null) return result;
        }
        return null;
    }
}
