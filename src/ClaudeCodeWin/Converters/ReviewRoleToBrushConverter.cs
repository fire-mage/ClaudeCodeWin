using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Converters;

public class ReviewRoleToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ReviewRole role)
        {
            var key = role switch
            {
                ReviewRole.Reviewer => "ReviewerBubbleBrush",
                ReviewRole.Driver => "DriverBubbleBrush",
                ReviewRole.Judge => "JudgeBubbleBrush",
                _ => "SurfaceLightBrush"
            };
            return Application.Current.FindResource(key) as Brush ?? Brushes.Gray;
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class ReviewRoleToAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ReviewRole role)
        {
            return role switch
            {
                ReviewRole.Reviewer => HorizontalAlignment.Left,
                ReviewRole.Driver => HorizontalAlignment.Right,
                ReviewRole.Judge => HorizontalAlignment.Center,
                ReviewRole.System => HorizontalAlignment.Center,
                _ => HorizontalAlignment.Left
            };
        }
        return HorizontalAlignment.Left;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
