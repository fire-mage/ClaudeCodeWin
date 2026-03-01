using System.Globalization;
using System.Windows.Data;

namespace ClaudeCodeWin.Converters;

public class ExpandArrowConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "\u25BE" : "\u25B8"; // ▾ and ▸

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
