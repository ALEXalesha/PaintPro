using System.Windows.Input;
using PaintPro.Commands;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Tools;

/// <summary>
/// Base for shape tools that rubber-band a single primitive: drag from p1 → p2.
/// Subclasses provide <see cref="DrawShape"/> which renders into the preview bitmap
/// (and into the final commit bitmap).
/// </summary>
public abstract class ShapeTool : ITool
{
    public abstract string Name { get; }
    public bool Fill { get; set; }

    private SKBitmap? _previewBitmap;
    private SKPoint _origin;
    private SKPoint _current;
    private bool _drawing;
    private float _strokeWidth;
    private SKColor _color;
    private byte _alpha = 255;

    public SKBitmap? PreviewBitmap => _drawing ? _previewBitmap : null;
    public byte PreviewAlpha => _alpha;
    public Cursor? GetCursor(SKPoint position) => Cursors.Cross;

    public void OnActivate(ToolContext ctx) { }
    public void OnDeactivate(ToolContext ctx)
    {
        if (_drawing) Reset(ctx);
    }

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        if (ctx.Document.ActiveLayer is not PixelLayer pl) return;
        if (_drawing) Reset(ctx);
        _origin = position;
        _current = position;
        // Render opaque, composite once with this alpha: a filled shape draws its fill and
        // its outline over the same pixels, and at partial opacity the overlap shows.
        _alpha = (byte)(255 * Math.Clamp(ctx.Opacity, 0f, 1f));
        _color = ctx.PrimaryColor.WithAlpha(255);
        _strokeWidth = MathF.Max(1f, ctx.ToolSize);

        _previewBitmap = new SKBitmap(pl.Width, pl.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(_previewBitmap)) c.Clear(SKColors.Transparent);

        _drawing = true;
        ctx.IsDrawing = true;
        ctx.Document.EnterTransientMode(DocumentMode.DrawingShape);
        Render();
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx)
    {
        if (!_drawing) return;
        _current = position;
        Render();
    }

    public void OnPointerUp(SKPoint position, ToolContext ctx)
    {
        if (!_drawing || _previewBitmap is null) return;
        _current = position;
        Render();

        var bbox = ComputeBBox();
        bbox.Inflate(_strokeWidth + 2f, _strokeWidth + 2f);
        var rect = SKRectI.Round(bbox);
        if (ctx.Document.ActiveLayer is PixelLayer pl)
            rect = SKRectI.Intersect(rect, new SKRectI(0, 0, pl.Width, pl.Height));

        if (!rect.IsEmpty)
        {
            var cropped = new SKBitmap(rect.Width, rect.Height, _previewBitmap.ColorType, _previewBitmap.AlphaType);
            using (var cc = new SKCanvas(cropped))
            {
                cc.DrawBitmap(_previewBitmap,
                    source: new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                    dest:   new SKRect(0, 0, rect.Width, rect.Height));
            }
            ctx.History.ExecuteAndPush(
                new DrawStrokeCommand(cropped, rect, SKBlendMode.SrcOver, _alpha), ctx.Document);
        }
        Reset(ctx);
    }

    private SKRect ComputeBBox()
        => new(MathF.Min(_origin.X, _current.X), MathF.Min(_origin.Y, _current.Y),
               MathF.Max(_origin.X, _current.X), MathF.Max(_origin.Y, _current.Y));

    private void Render()
    {
        if (_previewBitmap is null) return;
        using var c = new SKCanvas(_previewBitmap);
        c.Clear(SKColors.Transparent);
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Color = _color,
            StrokeWidth = _strokeWidth,
            Style = SKPaintStyle.Stroke,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap  = SKStrokeCap.Round,
        };
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Color = _color,
            Style = SKPaintStyle.Fill,
        };
        DrawShape(c, _origin, _current, stroke, Fill ? fill : null);
    }

    /// <summary>Subclasses render the shape into <paramref name="c"/> between p1 and p2.</summary>
    protected abstract void DrawShape(SKCanvas c, SKPoint p1, SKPoint p2, SKPaint stroke, SKPaint? fill);

    private void Reset(ToolContext ctx)
    {
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        _drawing = false;
        ctx.IsDrawing = false;
        if (ctx.Document.Mode == DocumentMode.DrawingShape)
            ctx.Document.EnterTransientMode(DocumentMode.Idle);
    }
}

public sealed class LineShapeTool : ShapeTool
{
    public override string Name => "Line";
    protected override void DrawShape(SKCanvas c, SKPoint p1, SKPoint p2, SKPaint stroke, SKPaint? fill)
        => c.DrawLine(p1, p2, stroke);
}

public sealed class RectShapeTool : ShapeTool
{
    public override string Name => "Rect";
    protected override void DrawShape(SKCanvas c, SKPoint p1, SKPoint p2, SKPaint stroke, SKPaint? fill)
    {
        var r = new SKRect(MathF.Min(p1.X, p2.X), MathF.Min(p1.Y, p2.Y),
                           MathF.Max(p1.X, p2.X), MathF.Max(p1.Y, p2.Y));
        if (fill is not null) c.DrawRect(r, fill);
        c.DrawRect(r, stroke);
    }
}

public sealed class EllipseShapeTool : ShapeTool
{
    public override string Name => "Ellipse";
    protected override void DrawShape(SKCanvas c, SKPoint p1, SKPoint p2, SKPaint stroke, SKPaint? fill)
    {
        var r = new SKRect(MathF.Min(p1.X, p2.X), MathF.Min(p1.Y, p2.Y),
                           MathF.Max(p1.X, p2.X), MathF.Max(p1.Y, p2.Y));
        if (fill is not null) c.DrawOval(r, fill);
        c.DrawOval(r, stroke);
    }
}

public sealed class TriangleShapeTool : ShapeTool
{
    public override string Name => "Triangle";
    protected override void DrawShape(SKCanvas c, SKPoint p1, SKPoint p2, SKPaint stroke, SKPaint? fill)
    {
        var r = new SKRect(MathF.Min(p1.X, p2.X), MathF.Min(p1.Y, p2.Y),
                           MathF.Max(p1.X, p2.X), MathF.Max(p1.Y, p2.Y));
        using var path = new SKPath();
        path.MoveTo((r.Left + r.Right) / 2f, r.Top);
        path.LineTo(r.Right, r.Bottom);
        path.LineTo(r.Left, r.Bottom);
        path.Close();
        if (fill is not null) c.DrawPath(path, fill);
        c.DrawPath(path, stroke);
    }
}

public sealed class StarShapeTool : ShapeTool
{
    public override string Name => "Star";
    protected override void DrawShape(SKCanvas c, SKPoint p1, SKPoint p2, SKPaint stroke, SKPaint? fill)
    {
        var cx = (p1.X + p2.X) / 2f;
        var cy = (p1.Y + p2.Y) / 2f;
        var rOuter = MathF.Max(2f, MathF.Min(MathF.Abs(p2.X - p1.X), MathF.Abs(p2.Y - p1.Y)) / 2f);
        var rInner = rOuter * 0.45f;
        using var path = new SKPath();
        for (int i = 0; i < 10; i++)
        {
            float a = -MathF.PI / 2f + i * MathF.PI / 5f;
            float r = (i % 2 == 0) ? rOuter : rInner;
            float x = cx + r * MathF.Cos(a);
            float y = cy + r * MathF.Sin(a);
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        path.Close();
        if (fill is not null) c.DrawPath(path, fill);
        c.DrawPath(path, stroke);
    }
}

public sealed class ArrowShapeTool : ShapeTool
{
    public override string Name => "Arrow";
    protected override void DrawShape(SKCanvas c, SKPoint p1, SKPoint p2, SKPaint stroke, SKPaint? fill)
    {
        c.DrawLine(p1, p2, stroke);
        var dx = p2.X - p1.X; var dy = p2.Y - p1.Y;
        var len = MathF.Sqrt(dx*dx + dy*dy);
        if (len < 1) return;
        var ux = dx / len; var uy = dy / len;
        var head = MathF.Max(8f, stroke.StrokeWidth * 4f);
        var ax = p2.X - ux*head;  var ay = p2.Y - uy*head;
        var wing = head * 0.6f;
        var left  = new SKPoint(ax + -uy*wing, ay + ux*wing);
        var right = new SKPoint(ax + uy*wing, ay + -ux*wing);
        using var path = new SKPath();
        path.MoveTo(p2);
        path.LineTo(left);
        path.LineTo(right);
        path.Close();
        var solid = new SKPaint { IsAntialias = true, Color = stroke.Color, Style = SKPaintStyle.Fill };
        c.DrawPath(path, solid);
        solid.Dispose();
    }
}

public sealed class HeartShapeTool : ShapeTool
{
    public override string Name => "Heart";
    protected override void DrawShape(SKCanvas c, SKPoint p1, SKPoint p2, SKPaint stroke, SKPaint? fill)
    {
        var r = new SKRect(MathF.Min(p1.X, p2.X), MathF.Min(p1.Y, p2.Y),
                           MathF.Max(p1.X, p2.X), MathF.Max(p1.Y, p2.Y));
        var w = r.Width; var h = r.Height;
        if (w < 4 || h < 4) return;
        using var path = new SKPath();
        path.MoveTo(r.Left + w / 2f, r.Bottom);
        path.CubicTo(r.Left + w * 1.1f, r.Top + h * 0.6f,
                     r.Left + w * 0.65f, r.Top - h * 0.05f,
                     r.Left + w / 2f, r.Top + h * 0.30f);
        path.CubicTo(r.Left + w * 0.35f, r.Top - h * 0.05f,
                     r.Left - w * 0.1f, r.Top + h * 0.6f,
                     r.Left + w / 2f, r.Bottom);
        path.Close();
        if (fill is not null) c.DrawPath(path, fill);
        c.DrawPath(path, stroke);
    }
}
