using System.Windows.Input;
using PaintPro.Commands;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Tools;

/// <summary>
/// Base for Pencil / Brush / Eraser / Marker. Manages a per-stroke offscreen
/// bitmap that grows to enclose the stroke; on PointerUp it crops and commits
/// a <see cref="DrawStrokeCommand"/>.
///
/// Subclasses override <see cref="ConfigurePaint"/> to set colour, width, blend.
/// </summary>
public abstract class StrokeToolBase : ITool
{
    public abstract string Name { get; }

    private SKBitmap? _strokeBitmap;
    private SKCanvas? _strokeCanvas;
    private SKPath? _path;
    private SKPaint? _paint;
    private SKPoint _last;
    private SKRect _strokeBounds;
    private SKRectI _canvasSize;
    private bool _drawing;
    private byte _strokeAlpha = 255;
    private SKBlendMode _mergeBlend = SKBlendMode.SrcOver;

    public SKBitmap? PreviewBitmap => _drawing ? _strokeBitmap : null;
    public byte PreviewAlpha => _strokeAlpha;

    /// <summary>
    /// Тот же режим, каким штрих ляжет на слой. Снимается один раз, на нажатии: слой во
    /// время штриха не меняется, а <see cref="MergeBlendMode"/> зависит от того, нижний он
    /// или нет.
    /// </summary>
    public SKBlendMode PreviewBlendMode => _mergeBlend;

    public virtual Cursor? GetCursor(SKPoint position) => Cursors.Cross;

    public virtual void OnActivate(ToolContext ctx) { /* nothing */ }

    public virtual void OnDeactivate(ToolContext ctx)
    {
        if (_drawing) CancelStroke(ctx);
    }

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        if (ctx.DrawTarget() is not { } pl) return;
        // Прошлый штрих мог не получить PointerUp - например, у него отобрали захват мыши.
        // Без сброса его битмап размером с холст просто терялся вместе со ссылкой.
        if (_drawing) ResetStroke(ctx);
        _canvasSize = new SKRectI(0, 0, pl.Width, pl.Height);
        _strokeBitmap = new SKBitmap(pl.Width, pl.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _strokeCanvas = new SKCanvas(_strokeBitmap);
        _strokeCanvas.Clear(SKColors.Transparent);
        _paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        ConfigurePaint(_paint, ctx);

        // Peel the alpha off the paint and draw the stroke opaque. The transparency is
        // re-applied once, when the finished stroke is merged into the layer — otherwise
        // every overlapping segment and round cap composites again and the stroke turns
        // into a chain of dark blobs.
        _strokeAlpha = _paint.Color.Alpha;
        _paint.Color = _paint.Color.WithAlpha(255);
        _mergeBlend = MergeBlendMode(ctx);

        _path = new SKPath();
        _path.MoveTo(position);
        // Draw an initial dot so single-click leaves a mark.
        using (var dot = new SKPaint
        {
            IsAntialias = true,
            Color = _paint.Color,
            Style = SKPaintStyle.Fill,
            BlendMode = _paint.BlendMode,
        })
        {
            _strokeCanvas.DrawCircle(position, _paint.StrokeWidth / 2f, dot);
        }

        _last = position;
        var radius = _paint.StrokeWidth / 2f + 1f;
        _strokeBounds = new SKRect(position.X - radius, position.Y - radius,
                                   position.X + radius, position.Y + radius);
        _drawing = true;
        ctx.IsDrawing = true;
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx)
    {
        if (!_drawing || _strokeCanvas is null || _path is null || _paint is null) return;
        _path.LineTo(position);
        _strokeCanvas.DrawLine(_last, position, _paint);
        _last = position;

        var radius = _paint.StrokeWidth / 2f + 1f;
        _strokeBounds.Left   = Math.Min(_strokeBounds.Left,   position.X - radius);
        _strokeBounds.Top    = Math.Min(_strokeBounds.Top,    position.Y - radius);
        _strokeBounds.Right  = Math.Max(_strokeBounds.Right,  position.X + radius);
        _strokeBounds.Bottom = Math.Max(_strokeBounds.Bottom, position.Y + radius);
    }

    public void OnPointerUp(SKPoint position, ToolContext ctx)
    {
        if (!_drawing || _strokeBitmap is null || _paint is null) return;

        // Crop the stroke bitmap to its bbox to keep the undo diff small.
        var bbox = SKRectI.Round(_strokeBounds);
        bbox = SKRectI.Intersect(bbox, _canvasSize);
        if (!bbox.IsEmpty)
        {
            var cropped = new SKBitmap(bbox.Width, bbox.Height, _strokeBitmap.ColorType, _strokeBitmap.AlphaType);
            using (var cc = new SKCanvas(cropped))
            {
                cc.DrawBitmap(_strokeBitmap,
                    source: new SKRect(bbox.Left, bbox.Top, bbox.Right, bbox.Bottom),
                    dest:   new SKRect(0, 0, bbox.Width, bbox.Height));
            }

            var cmd = new DrawStrokeCommand(cropped, bbox, MergeBlendMode(ctx), _strokeAlpha);
            ctx.History.ExecuteAndPush(cmd, ctx.Document);
        }

        ResetStroke(ctx);
    }

    private void CancelStroke(ToolContext ctx)
    {
        ResetStroke(ctx);
    }

    private void ResetStroke(ToolContext ctx)
    {
        _strokeCanvas?.Dispose(); _strokeCanvas = null;
        _strokeBitmap?.Dispose(); _strokeBitmap = null;
        _path?.Dispose(); _path = null;
        _paint?.Dispose(); _paint = null;
        _drawing = false;
        _mergeBlend = SKBlendMode.SrcOver;
        ctx.IsDrawing = false;
    }

    /// <summary>Subclasses set Color, StrokeWidth, BlendMode here.</summary>
    protected abstract void ConfigurePaint(SKPaint paint, ToolContext ctx);

    /// <summary>
    /// Blend mode used when the finished stroke is merged into the layer. Separate from
    /// the paint's own mode, which applies while drawing into the offscreen buffer — the
    /// eraser needs to draw a normal opaque shape there and carve it out only at merge.
    /// </summary>
    protected virtual SKBlendMode MergeBlendMode(ToolContext ctx) => SKBlendMode.SrcOver;

    /// <summary>True when the tool is painting on the document's bottom (opaque) layer.</summary>
    protected static bool OnBottomLayer(ToolContext ctx)
        => ctx.Document.Layers.Count > 0
           && ReferenceEquals(ctx.Document.Layers[0], ctx.Document.ActiveLayer);
}

/// <summary>Thin line. Width = size * 0.5, no smoothing pass.</summary>
public sealed class PencilTool : StrokeToolBase
{
    public override string Name => "Pencil";
    protected override void ConfigurePaint(SKPaint paint, ToolContext ctx)
    {
        paint.Color = ctx.PrimaryColor.WithAlpha((byte)(255 * ctx.Opacity));
        paint.StrokeWidth = MathF.Max(1f, ctx.ToolSize * 0.5f);
    }
}

/// <summary>Soft thick line. Width = size, round caps/joins (default).</summary>
public sealed class BrushTool : StrokeToolBase
{
    public override string Name => "Brush";
    protected override void ConfigurePaint(SKPaint paint, ToolContext ctx)
    {
        paint.Color = ctx.PrimaryColor.WithAlpha((byte)(255 * ctx.Opacity));
        paint.StrokeWidth = MathF.Max(1f, ctx.ToolSize);
    }
}

/// <summary>
/// Eraser. On the bottom layer it paints solid white, Paint-classic style — that layer
/// IS the paper. On any layer above it erases to transparency instead: painting white
/// there would punch an opaque hole straight through everything underneath.
/// </summary>
public sealed class EraserTool : StrokeToolBase
{
    public override string Name => "Eraser";

    protected override void ConfigurePaint(SKPaint paint, ToolContext ctx)
    {
        paint.Color = SKColors.White;
        paint.StrokeWidth = MathF.Max(1f, ctx.ToolSize);
    }

    // DstOut keeps the destination only where the stroke is transparent, i.e. the stroke
    // shape is subtracted from the layer.
    protected override SKBlendMode MergeBlendMode(ToolContext ctx)
        => OnBottomLayer(ctx) ? SKBlendMode.SrcOver : SKBlendMode.DstOut;
}

/// <summary>Marker — translucent stroke, capped at 40% so it always reads as a highlighter.</summary>
public sealed class MarkerTool : StrokeToolBase
{
    public override string Name => "Marker";
    protected override void ConfigurePaint(SKPaint paint, ToolContext ctx)
    {
        // The alpha set here is peeled off by the base class and applied once at merge
        // time, so overlapping segments within one stroke do not darken each other.
        paint.Color = ctx.PrimaryColor.WithAlpha((byte)(255 * MathF.Min(ctx.Opacity, 0.4f)));
        paint.StrokeWidth = MathF.Max(1f, ctx.ToolSize);
    }
}
