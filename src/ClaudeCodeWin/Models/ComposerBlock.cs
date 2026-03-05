using System.ComponentModel;

namespace ClaudeCodeWin.Models;

public enum ComposerBlockType { Text, InlineImage }

public abstract class ComposerBlock
{
    public ComposerBlockType Type { get; }
    protected ComposerBlock(ComposerBlockType type) => Type = type;
}

public class TextComposerBlock : ComposerBlock, INotifyPropertyChanged
{
    private string _text = "";

    public string Text
    {
        get => _text;
        set
        {
            if (_text != value)
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TextComposerBlock() : base(ComposerBlockType.Text) { }
    public TextComposerBlock(string text) : base(ComposerBlockType.Text) => _text = text;
}

public class ImageComposerBlock : ComposerBlock
{
    public FileAttachment Attachment { get; }
    public string FileName => Attachment.FileName;
    public string FilePath => Attachment.FilePath;

    // Fix: null guard — FileName/FilePath properties dereference Attachment without null check
    public ImageComposerBlock(FileAttachment attachment)
        : base(ComposerBlockType.InlineImage)
    {
        Attachment = attachment ?? throw new ArgumentNullException(nameof(attachment));
    }
}
