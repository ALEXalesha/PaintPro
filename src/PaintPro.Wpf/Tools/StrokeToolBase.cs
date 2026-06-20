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

    public SKBitmap? PreviewBitmap => _drawing ? _strokeBitmap : null;

    public virtual Cursor? GetCursor(SKPoint position) => Cursors.Cross;

    public virtual void OnActivate(ToolContext ctx) { /* nothing */ }

    public virtual void OnDeactivate(ToolContext ctx)
    {
        if (_drawing) CancelStroke(ctx);
    }

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        if (ctx.Document.ActiveLayer is not PixelLayer pl) return;
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

        _path = new SKPath();
        _path.MoveTo(position);
        // Draw an initial dot so single-click leaves a mark.
        _strokeCanvas.DrawCircle(position, _paint.StrokeWidth / 2f, new SKPaint
        {
            IsAntialias = true,
            Color = _paint.Color,
            Style = SKPaintStyle.Fill,
            BlendMode = _paint.BlendMode,
        });

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

            var cmd = new DrawStrokeCommand(cropped, bbox, _paint.BlendMode);
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
        ctx.IsDrawing = false;
    }

    /// <summary>Subclasses set Color, StrokeWidth, BlendMode here.</summary>
    protected abstract void ConfigurePaint(SKPaint paint, ToolContext ctx);
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

/// <summary>Eraser — paints solid white (Paint-classic, non-transparent).</summary>
public sealed class EraserTool : StrokeToolBase
{
    public override string Name => "Eraser";
    protected override void ConfigurePaint(SKPaint paint, ToolContext ctx)
    {
        paint.Color = SKColors.White;
        paint.StrokeWidth = MathF.Max(1f, ctx.ToolSize);
    }
}

/// <summary>Marker — translucent stroke; uses a separate alpha layer to avoid stacking.</summary>
public sealed class MarkerTool : StrokeToolBase
{
    public override string Name => "Marker";
    protected override void ConfigurePaint(SKPaint paint, ToolContext ctx)
    {
        // Draw fully opaque on the per-stroke bitmap; the merge step uses SrcOver
        // so a single stroke composites at the alpha below, but overlapping in the
        // same stroke doesn't darken.
        paint.Color = ctx.PrimaryColor.WithAlpha((byte)(255 * MathF.Min(ctx.Opacity, 0.4f)));
        paint.StrokeWidth = MathF.Max(1f, ctx.ToolSize);
    }
}
