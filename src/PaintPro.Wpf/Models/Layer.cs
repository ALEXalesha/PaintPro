using SkiaSharp;

namespace PaintPro.Models;

/// <summary>
/// Base class for document layers. Future blend modes / adjustment layers
/// will subclass this; v1 only has PixelLayer.
/// </summary>
public abstract class Layer : IDisposable
{
    /// <summary>
    /// Stable identity that survives the layer being rebuilt (resize / rotate / crop
    /// replace the object but keep the id). History commands address layers by this
    /// instead of by "whatever is active right now" — otherwise an undo after the user
    /// switched or deleted a layer lands its pixels on the wrong one.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Layer";
    public bool Visible { get; set; } = true;
    /// <summary>Opacity 0..1.</summary>
    public float Opacity { get; set; } = 1f;

    public abstract int Width { get; }
    public abstract int Height { get; }

    /// <summary>Composite this layer onto the given canvas at document coords.</summary>
    public abstract void Render(SKCanvas canvas);

    public abstract void Dispose();
}

/// <summary>
/// A raster (pixel) layer backed by an SKBitmap. The most common layer type.
/// </summary>
public sealed class PixelLayer : Layer
{
    private SKBitmap _bitmap;

    public PixelLayer(int width, int height, SKColor? fill = null)
    {
        _bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (fill is SKColor c) Clear(c);
    }

    public override int Width => _bitmap.Width;
    public override int Height => _bitmap.Height;

    public SKBitmap Bitmap => _bitmap;

    /// <summary>Erase the entire bitmap to a single colour (typically white for the background layer).</summary>
    public void Clear(SKColor color)
    {
        using var canvas = new SKCanvas(_bitmap);
        canvas.Clear(color);
    }

    /// <summary>True if every pixel equals the given colour. Used in tests.</summary>
    public bool IsAllColor(SKColor color)
    {
        // Stride may include padding — read row by row.
        int stride = _bitmap.RowBytes;
        var pixels = _bitmap.GetPixelSpan();
        for (int y = 0; y < _bitmap.Height; y++)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < _bitmap.Width; x++)
            {
                int o = rowOffset + x * 4; // BGRA8888
                if (pixels[o + 0] != color.Blue ||
                    pixels[o + 1] != color.Green ||
                    pixels[o + 2] != color.Red ||
                    pixels[o + 3] != color.Alpha)
                {
                    return false;
                }
            }
        }
        return true;
    }

    public bool IsAllWhite() => IsAllColor(SKColors.White);

    /// <summary>Copy the rectangular region into a new bitmap. Used by commands to snapshot a diff for undo.</summary>
    public SKBitmap ExtractRegion(SKRectI region)
    {
        var clipped = SKRectI.Intersect(region, new SKRectI(0, 0, Width, Height));
        if (clipped.IsEmpty)
            return new SKBitmap(1, 1);
        var dst = new SKBitmap(clipped.Width, clipped.Height, _bitmap.ColorType, _bitmap.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.DrawBitmap(_bitmap,
            source: new SKRect(clipped.Left, clipped.Top, clipped.Right, clipped.Bottom),
            dest: new SKRect(0, 0, clipped.Width, clipped.Height));
        return dst;
    }

    /// <summary>Paste a bitmap into this layer at the given top-left position.</summary>
    public void DrawBitmap(SKBitmap source, SKPoint topLeft)
    {
        using var canvas = new SKCanvas(_bitmap);
        canvas.DrawBitmap(source, topLeft);
    }

    public override void Render(SKCanvas canvas)
    {
        if (!Visible) return;
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Opacity)) };
        canvas.DrawBitmap(_bitmap, 0, 0, paint);
    }

    public override void Dispose() => _bitmap.Dispose();
}
