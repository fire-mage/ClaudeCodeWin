using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.ViewModels;

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

public class EditDiffLineTypeToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            EditDiffLineType.Added => "SuccessBrush",
            EditDiffLineType.Removed => "ErrorBrush",
            _ => "TextSecondaryBrush"
        };
        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class GitStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string ?? "";
        var key = text switch
        {
            _ when text.Contains("uncommitted") || text.Contains("unpushed") => "WarningBrush",
            _ when text.Contains("clean") => "SuccessBrush",
            _ => "TextSecondaryBrush" // "no git" or empty
        };
        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
