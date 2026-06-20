using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace PaintPro.Services;

/// <summary>Convert between Skia SKColor and WPF Color/Brush.</summary>
public static class SkiaBitmapBridge
{
    public static Color ToWpf(this SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
    public static SKColor ToSkia(this Color c) => new(c.R, c.G, c.B, c.A);
    public static SolidColorBrush ToBrush(this SKColor c) => new(c.ToWpf());

    /// <summary>Convert an SKBitmap to a frozen WPF BitmapSource (safe to use across threads).</summary>
    public static BitmapSource ToBitmapSource(this SKBitmap src)
    {
        var pixels = src.GetPixels();
        var bmp = BitmapSource.Create(src.Width, src.Height,
            96, 96, PixelFormats.Pbgra32, null,
            pixels, src.RowBytes * src.Height, src.RowBytes);
        bmp.Freeze();
        return bmp;
    }
}
