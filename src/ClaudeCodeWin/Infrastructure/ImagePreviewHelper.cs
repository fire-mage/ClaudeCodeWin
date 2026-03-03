using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeCodeWin.Infrastructure;

public static class ImagePreviewHelper
{
    public static void ShowPreviewWindow(Window owner, string filePath, string? title = null)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly
            };

            var previewWindow = new Window
            {
                Title = title ?? Path.GetFileName(filePath),
                Width = Math.Min(bitmap.PixelWidth + 40, 1200),
                Height = Math.Min(bitmap.PixelHeight + 60, 800),
                MinWidth = 300,
                MinHeight = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                Background = owner.TryFindResource("BackgroundBrush") as Brush ?? Brushes.Black,
                Content = new ScrollViewer
                {
                    Content = image,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(8)
                }
            };

            previewWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot open image:\n{ex.Message}", "Preview Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
