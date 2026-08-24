using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Erase a selection (rectangle or polygon) back to the background colour.
/// Snapshots the affected region's pixels before Execute so Undo restores them —
/// this is what makes Cut / Delete reversible (previously they bypassed history).
/// </summary>
public sealed class EraseRegionCommand : IDocumentCommand, IDisposable
{
    private readonly SKRectI _bounds;
    private readonly SKPoint[]? _polygon; // null => fill the whole bounds rect
    private readonly SKColor _fill;
    private Guid _layerId;
    private SKBitmap? _underlying;

    /// <summary>
    /// Область, которую команда снимает и возвращает. У прямоугольника она совпадает с
    /// <see cref="_bounds"/>: он рисуется по целым числам и за них не выходит. У
    /// многоугольника - шире на пиксель со всех сторон: его контур идёт по дробным
    /// координатам и сглаживается, а сглаживание задевает пиксели за границей габарита,
    /// округлённого к ближайшему целому.
    ///
    /// Пока снимок брался ровно по габариту, отмена возвращала не всё: Delete по
    /// многоугольнику, нарисованному в стороне от целых координат, оставлял после Ctrl+Z
    /// бледную кайму по краю - пиксели, которые стёрли, но не записали. Ту же поправку на
    /// сглаживание делает <see cref="Models.Document"/> для области подъёма.
    /// </summary>
    private SKRectI _snapshotBounds;

    public EraseRegionCommand(SKRectI bounds, SKPoint[]? polygon = null, SKColor? fill = null)
    {
        _bounds = bounds;
        _polygon = polygon;
        _fill = fill ?? SKColors.White;
    }

    public string DisplayName => "Erase selection";

    public long ApproximateBytes => Bytes(_underlying);

    private static long Bytes(SKBitmap? b) => b is null ? 0 : (long)b.RowBytes * b.Height;

    public void Execute(Document doc)
    {
        if (LayerTarget.Resolve(doc, ref _layerId) is not { } pl) return;
        if (_underlying is null)
        {
            _snapshotBounds = SnapshotBounds(pl);
            _underlying = pl.ExtractRegion(_snapshotBounds);
        }
        using var canvas = new SKCanvas(pl.Bitmap);
        // Src (not SrcOver) so erasing to a transparent fill on an upper layer actually
        // clears the pixels instead of compositing nothing over them.
        using var paint = new SKPaint
        {
            Color = _fill,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            BlendMode = SKBlendMode.Src,
        };
        if (_polygon is { Length: >= 3 } poly)
        {
            using var path = new SKPath();
            path.MoveTo(poly[0]);
            for (int i = 1; i < poly.Length; i++) path.LineTo(poly[i]);
            path.Close();
            canvas.DrawPath(path, paint);
        }
        else
        {
            canvas.DrawRect(new SKRect(_bounds.Left, _bounds.Top, _bounds.Right, _bounds.Bottom), paint);
        }
    }

    public void Dispose()
    {
        _underlying?.Dispose();
        _underlying = null;
    }

    public void Undo(Document doc)
    {
        if (LayerTarget.Resolve(doc, ref _layerId) is not { } pl || _underlying is null) return;
        var r = _snapshotBounds;
        using var canvas = new SKCanvas(pl.Bitmap);
        canvas.Save();
        canvas.ClipRect(new SKRect(r.Left, r.Top, r.Right, r.Bottom));
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(_underlying, new SKPoint(r.Left, r.Top));
        canvas.Restore();
    }

    /// <summary>Габарит снимка: у многоугольника - с запасом на сглаживание, и всегда внутри слоя.</summary>
    private SKRectI SnapshotBounds(PixelLayer pl)
    {
        var r = _bounds;
        if (_polygon is { Length: >= 3 })
            r = new SKRectI(r.Left - 1, r.Top - 1, r.Right + 1, r.Bottom + 1);
        return SKRectI.Intersect(r, new SKRectI(0, 0, pl.Width, pl.Height));
    }
}
