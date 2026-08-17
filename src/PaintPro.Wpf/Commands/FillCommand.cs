using System.Runtime.InteropServices;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Flood-fill at a seed point with the chosen colour.
/// Stores the affected rect's previous pixels for undo — flood fill can cover an
/// arbitrarily large region, so the bbox-diff is the practical minimum.
///
/// The fill runs over a raw copy of the layer's BGRA buffer, not through
/// SKBitmap.GetPixel/SetPixel. Those cross the managed/native boundary per pixel: a
/// full-canvas fill at 900×600 took ~700 ms that way, and that is before the second
/// pass that rebuilt the undo snapshot. On a photo-sized canvas it froze the UI for
/// tens of seconds.
/// </summary>
public sealed class FillCommand : IDocumentCommand
{
    private readonly SKPointI _seed;
    private readonly SKColor _newColor;
    private Guid _layerId;
    private SKBitmap? _previousRegion;
    private SKRectI _affectedBounds;

    public FillCommand(SKPointI seed, SKColor newColor)
    {
        _seed = seed;
        _newColor = newColor;
    }

    public string DisplayName => "Fill";

    public long ApproximateBytes => Bytes(_previousRegion);

    private static long Bytes(SKBitmap? b) => b is null ? 0 : (long)b.RowBytes * b.Height;

    public void Execute(Document doc)
    {
        if (LayerTarget.Resolve(doc, ref _layerId) is not { } pl) return;
        // A redo re-runs the whole fill; drop the snapshot from the previous run first.
        _previousRegion?.Dispose();
        _previousRegion = null;

        var bmp = pl.Bitmap;
        int w = bmp.Width, h = bmp.Height;
        if (_seed.X < 0 || _seed.X >= w || _seed.Y < 0 || _seed.Y >= h) return;

        int stride = bmp.RowBytes;
        int byteCount = stride * h;
        var pixels = bmp.GetPixels();
        if (pixels == IntPtr.Zero) return;

        var buffer = new byte[byteCount];
        Marshal.Copy(pixels, buffer, 0, byteCount);
        // Untouched copy the undo snapshot is cut from, so no second pass is needed to
        // reconstruct what the filled pixels used to be.
        var original = (byte[])buffer.Clone();

        int seedOffset = _seed.Y * stride + _seed.X * 4;
        var target = ReadPixel(buffer, seedOffset);
        var fill = Premultiply(_newColor);
        if (Same(target, fill)) return;

        var bounds = ScanlineFill(buffer, w, h, stride, _seed, target, fill);
        if (bounds.IsEmpty) return;

        Marshal.Copy(buffer, 0, pixels, byteCount);
        bmp.NotifyPixelsChanged();

        _affectedBounds = bounds;
        _previousRegion = CropBuffer(original, stride, bounds, bmp.ColorType, bmp.AlphaType);
    }

    public void Undo(Document doc)
    {
        if (LayerTarget.Resolve(doc, ref _layerId) is not { } pl || _previousRegion is null) return;
        using var canvas = new SKCanvas(pl.Bitmap);
        // Clear before blitting: on a transparent layer a plain SrcOver draw of the
        // snapshot composites *under* the fill and leaves it visible.
        canvas.Save();
        canvas.ClipRect(new SKRect(_affectedBounds.Left, _affectedBounds.Top,
                                   _affectedBounds.Right, _affectedBounds.Bottom));
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(_previousRegion, new SKPoint(_affectedBounds.Left, _affectedBounds.Top));
        canvas.Restore();
    }

    /// <summary>
    /// Span-based flood fill: each iteration claims a whole horizontal run and only then
    /// looks at the rows above and below. That queues one entry per run instead of one
    /// per pixel, which is where most of the old version's time went.
    /// Returns the bounding box of everything it changed.
    /// </summary>
    private static SKRectI ScanlineFill(
        byte[] buf, int w, int h, int stride, SKPointI seed,
        (byte B, byte G, byte R, byte A) target, (byte B, byte G, byte R, byte A) fill)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        var stack = new Stack<(int X, int Y)>();
        stack.Push((seed.X, seed.Y));

        while (stack.Count > 0)
        {
            var (sx, sy) = stack.Pop();
            int row = sy * stride;
            if (!Same(ReadPixel(buf, row + sx * 4), target)) continue;

            // Walk left and right to the ends of this run.
            int left = sx;
            while (left > 0 && Same(ReadPixel(buf, row + (left - 1) * 4), target)) left--;
            int right = sx;
            while (right < w - 1 && Same(ReadPixel(buf, row + (right + 1) * 4), target)) right++;

            for (int x = left; x <= right; x++) WritePixel(buf, row + x * 4, fill);

            if (left < minX) minX = left;
            if (right > maxX) maxX = right;
            if (sy < minY) minY = sy;
            if (sy > maxY) maxY = sy;

            // Seed the neighbouring rows once per contiguous stretch, not once per pixel.
            if (sy > 0) SeedRow(buf, stride, left, right, sy - 1, target, stack);
            if (sy < h - 1) SeedRow(buf, stride, left, right, sy + 1, target, stack);
        }

        if (maxX < minX || maxY < minY) return SKRectI.Empty;
        return new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    private static void SeedRow(byte[] buf, int stride, int left, int right, int y,
        (byte B, byte G, byte R, byte A) target, Stack<(int X, int Y)> stack)
    {
        int row = y * stride;
        bool inRun = false;
        for (int x = left; x <= right; x++)
        {
            bool match = Same(ReadPixel(buf, row + x * 4), target);
            if (match && !inRun) { stack.Push((x, y)); inRun = true; }
            else if (!match) inRun = false;
        }
    }

    private static (byte B, byte G, byte R, byte A) ReadPixel(byte[] buf, int offset)
        => (buf[offset], buf[offset + 1], buf[offset + 2], buf[offset + 3]);

    private static void WritePixel(byte[] buf, int offset, (byte B, byte G, byte R, byte A) c)
    {
        buf[offset] = c.B; buf[offset + 1] = c.G; buf[offset + 2] = c.R; buf[offset + 3] = c.A;
    }

    private static bool Same((byte B, byte G, byte R, byte A) a, (byte B, byte G, byte R, byte A) b)
        => a.B == b.B && a.G == b.G && a.R == b.R && a.A == b.A;

    /// <summary>Layers are Bgra8888/Premul, so the fill colour has to be premultiplied to match.</summary>
    private static (byte B, byte G, byte R, byte A) Premultiply(SKColor c)
    {
        byte a = c.Alpha;
        return ((byte)(c.Blue * a / 255), (byte)(c.Green * a / 255), (byte)(c.Red * a / 255), a);
    }

    private static SKBitmap CropBuffer(byte[] source, int stride, SKRectI region,
        SKColorType colorType, SKAlphaType alphaType)
    {
        var dst = new SKBitmap(region.Width, region.Height, colorType, alphaType);
        var dstPixels = dst.GetPixels();
        int dstStride = dst.RowBytes;
        for (int y = 0; y < region.Height; y++)
        {
            int srcOffset = (region.Top + y) * stride + region.Left * 4;
            Marshal.Copy(source, srcOffset, dstPixels + y * dstStride, region.Width * 4);
        }
        dst.NotifyPixelsChanged();
        return dst;
    }
}
