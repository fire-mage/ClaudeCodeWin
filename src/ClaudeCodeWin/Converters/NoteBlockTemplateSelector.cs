using System.Windows;
using System.Windows.Controls;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Converters;

public class NoteBlockTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? ImageTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is NoteBlock block)
            return block.Type == NoteBlockType.Image ? ImageTemplate : TextTemplate;
        return base.SelectTemplate(item, container);
    }
}
