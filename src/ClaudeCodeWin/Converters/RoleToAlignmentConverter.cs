using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Converters;

public class RoleToAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            MessageRole.User => HorizontalAlignment.Right,
            MessageRole.System => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Left
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class RoleToBubbleBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            MessageRole.User => "UserBubbleBrush",
            MessageRole.System => "SystemBubbleBrush",
            _ => "AssistantBubbleBrush"
        };

        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class RoleToMaxWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is MessageRole role && role == MessageRole.User
            ? 500.0
            : double.PositiveInfinity;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
