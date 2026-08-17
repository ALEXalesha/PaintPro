using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Resize the document's canvas, preserving existing pixels (top-left anchored).
/// New area on the bottom layer is filled with the background colour; layers above it
/// stay transparent so they keep letting the layers below show through.
/// </summary>
public sealed class ResizeCanvasCommand : IDocumentCommand
{
    /// <summary>Hard ceiling on either dimension, and on total pixels, to keep a typo from OOM-ing the app.</summary>
    public const int MaxDimension = 20000;
    public const long MaxPixels = 120_000_000;

    private readonly int _newWidth;
    private readonly int _newHeight;
    private readonly SKColor _backgroundColor;

    private int _previousWidth;
    private int _previousHeight;
    private SKBitmap[]? _previousLayers;

    public ResizeCanvasCommand(int newWidth, int newHeight, SKColor? backgroundColor = null)
    {
        _newWidth = Math.Clamp(newWidth, 1, MaxDimension);
        _newHeight = Math.Clamp(newHeight, 1, MaxDimension);
        _backgroundColor = backgroundColor ?? SKColors.White;
    }

    /// <summary>True if the requested size is within the limits this command enforces.</summary>
    public static bool IsAllowed(int width, int height)
        => width >= 1 && height >= 1
           && width <= MaxDimension && height <= MaxDimension
           && (long)width * height <= MaxPixels;

    public string DisplayName => "Resize canvas";

    public long ApproximateBytes
    {
        get
        {
            if (_previousLayers is null) return 0;
            long sum = 0;
            foreach (var b in _previousLayers) sum += (long)b.RowBytes * b.Height;
            return sum;
        }
    }

    public void Execute(Document doc)
    {
        _previousWidth = doc.CanvasWidth;
        _previousHeight = doc.CanvasHeight;
        _previousLayers ??= Snapshot(doc);

        Rebuild(doc, _newWidth, _newHeight, null);
        doc.CanvasWidth = _newWidth;
        doc.CanvasHeight = _newHeight;
    }

    public void Undo(Document doc)
    {
        Rebuild(doc, _previousWidth, _previousHeight, _previousLayers);
        doc.CanvasWidth = _previousWidth;
        doc.CanvasHeight = _previousHeight;
    }

    private static SKBitmap[] Snapshot(Document doc)
    {
        var shots = new SKBitmap[doc.Layers.Count];
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            shots[i] = doc.Layers[i] is PixelLayer pl
                ? pl.ExtractRegion(new SKRectI(0, 0, pl.Width, pl.Height))
                : new SKBitmap(1, 1);
        }
        return shots;
    }

    /// <summary>
    /// Rebuild every layer at (w,h). Content comes from <paramref name="restore"/> when
    /// undoing (so pixels cropped away by the resize come back), otherwise from the
    /// layer's own current bitmap.
    /// </summary>
    private void Rebuild(Document doc, int w, int h, SKBitmap[]? restore)
    {
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            if (doc.Layers[i] is not PixelLayer old) continue;
            var fill = i == 0 ? _backgroundColor : SKColors.Transparent;
            var newLayer = new PixelLayer(w, h, fill)
            {
                Id = old.Id,
                Name = old.Name,
                Visible = old.Visible,
                Opacity = old.Opacity,
            };
            var content = restore is not null && i < restore.Length ? restore[i] : old.Bitmap;
            using (var canvas = new SKCanvas(newLayer.Bitmap)) canvas.DrawBitmap(content, 0, 0);
            old.Dispose();
            doc.Layers[i] = newLayer;
        }
    }
}
