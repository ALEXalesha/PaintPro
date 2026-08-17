using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Composite a stroke (already pre-rendered onto a small bitmap) onto its layer.
/// Snapshots the underlying pixels once before Execute so Undo can restore them.
/// This is the diff-only undo strategy from antipattern #5.
///
/// The stroke bitmap is drawn fully opaque by the tool; <paramref name="alpha"/> is
/// applied here, at merge time. Doing it the other way round makes every overlapping
/// segment of a single semi-transparent stroke darken the one below it.
/// </summary>
public sealed class DrawStrokeCommand : IDocumentCommand
{
    private readonly SKBitmap _strokeBitmap;
    private readonly SKRectI _bounds;
    private readonly SKBlendMode _blendMode;
    private readonly byte _alpha;
    private Guid _layerId;
    private SKBitmap? _underlying;

    public DrawStrokeCommand(SKBitmap stroke, SKRectI bounds,
        SKBlendMode blend = SKBlendMode.SrcOver, byte alpha = 255)
    {
        _strokeBitmap = stroke;
        _bounds = bounds;
        _blendMode = blend;
        _alpha = alpha;
    }

    public string DisplayName => "Draw stroke";

    public void Execute(Document doc)
    {
        if (LayerTarget.Resolve(doc, ref _layerId) is not { } pl) return;
        _underlying ??= pl.ExtractRegion(_bounds);
        using var canvas = new SKCanvas(pl.Bitmap);
        using var paint = new SKPaint
        {
            BlendMode = _blendMode,
            Color = SKColors.White.WithAlpha(_alpha),
        };
        canvas.DrawBitmap(_strokeBitmap, new SKPoint(_bounds.Left, _bounds.Top), paint);
    }

    public void Undo(Document doc)
    {
        if (LayerTarget.Resolve(doc, ref _layerId) is not { } pl || _underlying is null) return;
        using var canvas = new SKCanvas(pl.Bitmap);
        // Clear destination first so an alpha-aware stroke restores correctly.
        canvas.Save();
        canvas.ClipRect(new SKRect(_bounds.Left, _bounds.Top, _bounds.Right, _bounds.Bottom));
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(_underlying, new SKPoint(_bounds.Left, _bounds.Top));
        canvas.Restore();
    }
}
