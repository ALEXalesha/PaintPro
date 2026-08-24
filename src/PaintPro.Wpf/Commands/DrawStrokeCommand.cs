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
public sealed class DrawStrokeCommand : IDocumentCommand, IDisposable
{
    private readonly SKBitmap _strokeBitmap;
    private readonly SKRectI _bounds;
    private readonly SKBlendMode _blendMode;
    private readonly byte _alpha;
    private Guid _layerId;
    private SKBitmap? _underlying;

    /// <param name="layerId">
    /// Слой, в который штрих обязан лечь. <see cref="Guid.Empty"/> - «тот, что активен
    /// сейчас», и это правильный ответ только в момент нажатия. Инструменты снимают слой
    /// на нажатии и передают его сюда: жест начинается на одном слое, а исполняется
    /// командой на отпускании, и между этими двумя мгновениями активный слой уже может
    /// быть другим - тогда штрих, который пользователь вёл по верхнему слою, оказывался
    /// на нижнем, а ластик вместо дыры красил белым.
    /// </param>
    public DrawStrokeCommand(SKBitmap stroke, SKRectI bounds,
        SKBlendMode blend = SKBlendMode.SrcOver, byte alpha = 255, Guid layerId = default)
    {
        _strokeBitmap = stroke;
        _bounds = bounds;
        _blendMode = blend;
        _alpha = alpha;
        _layerId = layerId;
    }

    public string DisplayName => "Draw stroke";

    public long ApproximateBytes => Bytes(_strokeBitmap) + Bytes(_underlying);

    private static long Bytes(SKBitmap? b) => b is null ? 0 : (long)b.RowBytes * b.Height;

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

    /// <summary>Запись вытеснена из истории: её битмапы больше никому не нужны.</summary>
    public void Dispose()
    {
        _strokeBitmap.Dispose();
        _underlying?.Dispose();
        _underlying = null;
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
