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
    /// <summary>
    /// The selection rect inside <paramref name="doc"/>, or the full canvas if there is no
    /// selection — flattened. Copy takes what the user can see; reading the active layer
    /// alone put an unexpectedly empty or partial image on the clipboard as soon as the
    /// document had more than one layer.
    /// </summary>
    private static SKBitmap ExtractSelectedRegion(Document doc)
    {
        var canvasRect = new SKRectI(0, 0, doc.CanvasWidth, doc.CanvasHeight);
        // Плавающий объект по инварианту обнуляет Selection, поэтому ветка "нет
        // выделения - копируем весь холст" срабатывала сразу после того, как
        // выделение подняли для перемещения: Ctrl+C по перетащенной картинке
        // клал в буфер обмена весь документ.
        SKRectI rect = doc.Selection switch
        {
            RectSelection rs => SKRectI.Round(rs.Rect),
            PolygonSelection poly => SKRectI.Round(poly.BoundingBox),
            _ when doc.FloatingPickup is { } fp
                => Document.PickupBounds(fp, doc.CanvasWidth, doc.CanvasHeight),
            _ => canvasRect,
        };
        rect = SKRectI.Intersect(rect, canvasRect);
        if (rect.IsEmpty) return new SKBitmap(1, 1);

        using var flat = FileService.Flatten(doc);
        var dst = new SKBitmap(rect.Width, rect.Height, flat.ColorType, flat.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.DrawBitmap(flat,
            source: new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
            dest: new SKRect(0, 0, rect.Width, rect.Height));
        return dst;
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
