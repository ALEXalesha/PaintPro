using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Flood-fill at a seed point with the chosen colour.
/// Stores the entire affected rect's previous pixels for undo — flood fill
/// can cover an arbitrarily large region, so the bbox-diff is the practical minimum.
/// </summary>
public sealed class FillCommand : IDocumentCommand
{
    private readonly SKPointI _seed;
    private readonly SKColor _newColor;
    private SKBitmap? _previousRegion;
    private SKRectI _affectedBounds;

    public FillCommand(SKPointI seed, SKColor newColor)
    {
        _seed = seed;
        _newColor = newColor;
    }

    public string DisplayName => "Fill";

    public void Execute(Document doc)
    {
        if (doc.ActiveLayer is not PixelLayer pl) return;
        var bmp = pl.Bitmap;
        int w = bmp.Width, h = bmp.Height;
        if (_seed.X < 0 || _seed.X >= w || _seed.Y < 0 || _seed.Y >= h) return;

        var target = bmp.GetPixel(_seed.X, _seed.Y);
        if (target == _newColor) return;

        // BFS / scanline flood fill.
        int minX = _seed.X, maxX = _seed.X, minY = _seed.Y, maxY = _seed.Y;
        var visited = new bool[w * h];
        var queue = new Queue<SKPointI>();
        queue.Enqueue(_seed);

        // Snapshot pixels we change — collect into a temporary buffer indexed by (x,y).
        // For simplicity we'll snapshot the entire bbox AFTER filling, which is cheaper
        // than tracking each changed pixel.
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            int idx = p.Y * w + p.X;
            if (visited[idx]) continue;
            visited[idx] = true;
            if (bmp.GetPixel(p.X, p.Y) != target) continue;
            bmp.SetPixel(p.X, p.Y, _newColor);
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;

            if (p.X > 0)         queue.Enqueue(new SKPointI(p.X - 1, p.Y));
            if (p.X < w - 1)     queue.Enqueue(new SKPointI(p.X + 1, p.Y));
            if (p.Y > 0)         queue.Enqueue(new SKPointI(p.X, p.Y - 1));
            if (p.Y < h - 1)     queue.Enqueue(new SKPointI(p.X, p.Y + 1));
        }

        _affectedBounds = new SKRectI(minX, minY, maxX + 1, maxY + 1);
        // Snapshot AFTER fill is the "fill result" — we actually need the BEFORE pixels,
        // so we must rebuild them. Cheap trick: for every pixel in bounds, set back to target
        // if it matches new colour and was visited, otherwise keep current. We DID change every
        // visited matching pixel to _newColor, so the BEFORE region was visited-cells = target,
        // non-visited cells = whatever they currently are.
        _previousRegion = new SKBitmap(_affectedBounds.Width, _affectedBounds.Height, bmp.ColorType, bmp.AlphaType);
        using var rc = new SKCanvas(_previousRegion);
        // Start from current state of region.
        rc.DrawBitmap(bmp,
            source: new SKRect(_affectedBounds.Left, _affectedBounds.Top, _affectedBounds.Right, _affectedBounds.Bottom),
            dest:   new SKRect(0, 0, _affectedBounds.Width, _affectedBounds.Height));
        // Then overwrite visited cells with the original target colour.
        for (int y = _affectedBounds.Top; y < _affectedBounds.Bottom; y++)
        for (int x = _affectedBounds.Left; x < _affectedBounds.Right; x++)
        {
            if (visited[y * w + x])
                _previousRegion.SetPixel(x - _affectedBounds.Left, y - _affectedBounds.Top, target);
        }
    }

    public void Undo(Document doc)
    {
        if (doc.ActiveLayer is not PixelLayer pl || _previousRegion is null) return;
        using var canvas = new SKCanvas(pl.Bitmap);
        canvas.DrawBitmap(_previousRegion, new SKPoint(_affectedBounds.Left, _affectedBounds.Top));
    }
}
