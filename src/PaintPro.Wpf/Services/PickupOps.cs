using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// Shared lift/erase logic for floating pickups.
///
/// SelectTool, QuadTool and the canvas handle-drag code all need the same two operations,
/// and used to carry three near-identical copies of the lazy erase. One copy means the
/// source layer, the erase colour and the OriginalQuad snapshot stay consistent between
/// them.
/// </summary>
public static class PickupOps
{
    /// <summary>
    /// Colour the source area is erased to. The bottom layer is the opaque paper of the
    /// document, so it goes back to white; anything above must become transparent or it
    /// would punch a white hole through the layers below.
    /// </summary>
    public static SKColor EraseColor(Document doc, Layer layer)
        => doc.Layers.Count > 0 && ReferenceEquals(doc.Layers[0], layer)
            ? SKColors.White
            : SKColors.Transparent;

    /// <summary>Lift a rectangular region of the active layer into a floating pickup.</summary>
    public static void PromoteRect(Document doc, SKRect rect)
        => Promote(doc, rect, quad: null);

    /// <summary>Lift a 4-point region: the bbox pixels are lifted, the polygon becomes the clip.</summary>
    public static void PromoteQuad(Document doc, IReadOnlyList<SKPoint> corners)
        => Promote(doc, Bounds(corners), corners.ToArray());

    private static void Promote(Document doc, SKRect rect, SKPoint[]? quad)
    {
        if (doc.ActiveLayer is not PixelLayer pl) return;
        var clamped = SKRectI.Intersect(SKRectI.Round(rect), new SKRectI(0, 0, pl.Width, pl.Height));
        if (clamped.IsEmpty) return;

        using var raw = pl.ExtractRegion(clamped);
        // Lift only the drawn marks: the white canvas background is keyed out so moving
        // the selection doesn't drag an opaque white box over whatever sits underneath.
        var pickupBitmap = BitmapKeying.KeyOutBackground(raw, SKColors.White);
        var pickup = new FloatingPickup(pickupBitmap,
            new SKRect(clamped.Left, clamped.Top, clamped.Right, clamped.Bottom))
        {
            Quad = quad,
            // Форма на момент подъёма - именно её выкусывают из слоя. Пока она
            // снималась при первом перемещении, перетаскивание угла ДО перемещения
            // подменяло её: из слоя вырезалась новая форма, то есть область, которую
            // пользователь не выделял, а часть выделенной оставалась лежать на месте.
            OriginalQuad = quad is null ? null : (SKPoint[])quad.Clone(),
            SourceLayerId = pl.Id,
            // Snapshot the layer as it is now (before any lazy-erase) so the eventual
            // commit can record an undoable before/after diff, and Escape can put the
            // lifted pixels back.
            PreEditSnapshot = pl.ExtractRegion(new SKRectI(0, 0, pl.Width, pl.Height)),
        };
        // С чем сравнивать при коммите: подъём сам по себе холста не меняет, и отличить
        // «подняли и положили обратно» от настоящего перемещения можно только так.
        pickup.RememberOrigin();
        doc.FloatingPickup = pickup;
    }

    /// <summary>
    /// Antipattern §6: erase the original area on the source layer at the FIRST
    /// move/scale/rotate, never at lift time. Idempotent via OriginalAreaErased.
    /// </summary>
    public static void EnsureLazyErase(Document doc, FloatingPickup fp)
    {
        if (fp.OriginalAreaErased) return;
        var pl = doc.FindPixelLayer(fp.SourceLayerId) ?? doc.ActiveLayer as PixelLayer;
        if (pl is null) return;

        using var canvas = new SKCanvas(pl.Bitmap);
        using var paint = new SKPaint
        {
            Color = EraseColor(doc, pl),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            // Src so an erase to transparent actually clears instead of compositing nothing.
            BlendMode = SKBlendMode.Src,
        };

        // Выкусываем форму, которую подняли, а не ту, что на пикапе сейчас: перетаскивание
        // угла меняет маску, но не то, что было взято из слоя.
        if ((fp.OriginalQuad ?? fp.Quad) is { } quad)
        {
            fp.OriginalQuad ??= (SKPoint[])quad.Clone();
            using var path = new SKPath();
            path.MoveTo(quad[0]);
            path.LineTo(quad[1]);
            path.LineTo(quad[2]);
            path.LineTo(quad[3]);
            path.Close();
            canvas.DrawPath(path, paint);
        }
        else
        {
            canvas.DrawRect(fp.OriginalBBox, paint);
        }
        fp.OriginalAreaErased = true;
    }

    /// <summary>Index of the corner within <paramref name="radius"/> document px of the point, or -1.</summary>
    public static int HitCorner(IReadOnlyList<SKPoint> corners, SKPoint p, float radius)
    {
        float best = radius * radius;
        int hit = -1;
        for (int i = 0; i < corners.Count; i++)
        {
            var dx = corners[i].X - p.X;
            var dy = corners[i].Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 <= best) { best = d2; hit = i; }
        }
        return hit;
    }

    private static SKRect Bounds(IReadOnlyList<SKPoint> pts)
    {
        float minX = pts[0].X, maxX = pts[0].X, minY = pts[0].Y, maxY = pts[0].Y;
        for (int i = 1; i < pts.Count; i++)
        {
            if (pts[i].X < minX) minX = pts[i].X;
            if (pts[i].X > maxX) maxX = pts[i].X;
            if (pts[i].Y < minY) minY = pts[i].Y;
            if (pts[i].Y > maxY) maxY = pts[i].Y;
        }
        return new SKRect(minX, minY, maxX, maxY);
    }
}
