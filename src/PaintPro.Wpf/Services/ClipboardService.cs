using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// System clipboard bridge for raster images. Uses WPF's Clipboard API and converts
/// to/from SkiaSharp via PNG round-trip (the safest cross-app format).
/// </summary>
public sealed class ClipboardService
{
    /// <summary>Returns the selection rect inside <paramref name="doc"/>, or the full canvas if no selection.</summary>
    private static SKBitmap ExtractSelectedRegion(Document doc)
    {
        if (doc.ActiveLayer is not PixelLayer pl)
            return new SKBitmap(1, 1);

        SKRectI rect;
        if (doc.Selection is RectSelection rs)
            rect = SKRectI.Round(rs.Rect);
        else if (doc.Selection is PolygonSelection poly)
            rect = SKRectI.Round(poly.BoundingBox);
        else
            rect = new SKRectI(0, 0, pl.Width, pl.Height);

        rect = SKRectI.Intersect(rect, new SKRectI(0, 0, pl.Width, pl.Height));
        return pl.ExtractRegion(rect);
    }

    /// <summary>Copy the current selection (or whole canvas) to the OS clipboard.</summary>
    public void Copy(Document doc)
    {
        using var bmp = ExtractSelectedRegion(doc);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        Clipboard.SetImage(bi);
    }

    /// <summary>Try to read a bitmap from the clipboard. Returns null if no image.</summary>
    public SKBitmap? TryGetImage()
    {
        if (!Clipboard.ContainsImage()) return null;
        var src = Clipboard.GetImage();
        if (src is null) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        return SKBitmap.Decode(ms);
    }
}
