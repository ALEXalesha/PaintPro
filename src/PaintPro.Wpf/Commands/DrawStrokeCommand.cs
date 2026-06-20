using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Composite a stroke (already pre-rendered onto a small bitmap) onto the active layer.
/// Snapshots the underlying pixels once before Execute so Undo can restore them.
/// This is the diff-only undo strategy from antipattern #5.
/// </summary>
public sealed class DrawStrokeCommand : IDocumentCommand
{
    private readonly SKBitmap _strokeBitmap;
    private readonly SKRectI _bounds;
    private SKBitmap? _underlying;
    private readonly SKBlendMode _blendMode;

    public DrawStrokeCommand(SKBitmap stroke, SKRectI bounds, SKBlendMode blend = SKBlendMode.SrcOver)
    {
        _strokeBitmap = stroke;
        _bounds = bounds;
        _blendMode = blend;
    }

    public string DisplayName => "Draw stroke";

    public void Execute(Document doc)
    {
        if (doc.ActiveLayer is not PixelLayer pl) return;
        _underlying ??= pl.ExtractRegion(_bounds);
        using var canvas = new SKCanvas(pl.Bitmap);
        using var paint = new SKPaint { BlendMode = _blendMode };
        canvas.DrawBitmap(_strokeBitmap, new SKPoint(_bounds.Left, _bounds.Top), paint);
    }

    public void Undo(Document doc)
    {
        if (doc.ActiveLayer is not PixelLayer pl || _underlying is null) return;
        using var canvas = new SKCanvas(pl.Bitmap);
        // Clear destination first so an alpha-aware stroke restores correctly.
        canvas.Save();
        canvas.ClipRect(new SKRect(_bounds.Left, _bounds.Top, _bounds.Right, _bounds.Bottom));
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(_underlying, new SKPoint(_bounds.Left, _bounds.Top));
        canvas.Restore();
    }
}
