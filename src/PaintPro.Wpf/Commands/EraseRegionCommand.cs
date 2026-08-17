using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Erase a selection (rectangle or polygon) back to the background colour.
/// Snapshots the affected region's pixels before Execute so Undo restores them —
/// this is what makes Cut / Delete reversible (previously they bypassed history).
/// </summary>
public sealed class EraseRegionCommand : IDocumentCommand
{
    private readonly SKRectI _bounds;
    private readonly SKPoint[]? _polygon; // null => fill the whole bounds rect
    private readonly SKColor _fill;
    private Guid _layerId;
    private SKBitmap? _underlying;

    public EraseRegionCommand(SKRectI bounds, SKPoint[]? polygon = null, SKColor? fill = null)
    {
        _bounds = bounds;
        _polygon = polygon;
        _fill = fill ?? SKColors.White;
    }

    public string DisplayName => "Erase selection";

    public void Execute(Document doc)
    {
        if (LayerTarget.Resolve(doc, ref _layerId) is not { } pl) return;
        _underlying ??= pl.ExtractRegion(_bounds);
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

    public void Undo(Document doc)
    {
        if (LayerTarget.Resolve(doc, ref _layerId) is not { } pl || _underlying is null) return;
        using var canvas = new SKCanvas(pl.Bitmap);
        canvas.Save();
        canvas.ClipRect(new SKRect(_bounds.Left, _bounds.Top, _bounds.Right, _bounds.Bottom));
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(_underlying, new SKPoint(_bounds.Left, _bounds.Top));
        canvas.Restore();
    }
}
