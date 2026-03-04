using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClaudeCodeWin.Converters;

/// <summary>
/// Converts null/empty values to Collapsed, non-null to Visible.
/// For strings, also treats empty/whitespace as Collapsed.
/// </summary>
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s)
            return string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
        return value is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
