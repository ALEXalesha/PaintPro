using System.Windows.Input;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Tools;

/// <summary>
/// 4-point polygon selection. Initial drag creates an axis-aligned rect (4 corners),
/// then individual corners can be dragged independently to reshape the clip mask.
/// Content is NOT warped; the polygon acts as the clip path when committing/rendering.
///
/// On the FIRST translate/scale/rotate (not corner-drag) the active layer is erased
/// using the CURRENT polygon shape, not its bbox — see antipattern §6.
/// </summary>
public sealed class QuadTool : ITool
{
    public string Name => "Quad";
    public SKBitmap? PreviewBitmap => null;
    public Cursor? GetCursor(SKPoint position) => Cursors.Cross;

    private SKPoint _origin;
    private bool _isCreating;
    private bool _isMovingFloating;
    private SKPoint _lastMove;

    public void OnActivate(ToolContext ctx) { }
    public void OnDeactivate(ToolContext ctx) => ctx.Document.CommitFloating();

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        var doc = ctx.Document;

        if (doc.FloatingPickup is { } fp && fp.CurrentBBox.Contains(position))
        {
            _isMovingFloating = true;
            _lastMove = position;
            ctx.IsDrawing = true;
            return;
        }

        if (doc.FloatingPickup is not null)
            doc.CommitFloating();

        _origin = position;
        _isCreating = true;
        var rect = new SKRect(position.X, position.Y, position.X, position.Y);
        doc.Selection = new PolygonSelection(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Right, rect.Top),
            new SKPoint(rect.Right, rect.Bottom),
            new SKPoint(rect.Left, rect.Bottom));
        ctx.IsDrawing = true;
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx)
    {
        if (_isCreating)
        {
            var r = new SKRect(
                MathF.Min(_origin.X, position.X), MathF.Min(_origin.Y, position.Y),
                MathF.Max(_origin.X, position.X), MathF.Max(_origin.Y, position.Y));
            ctx.Document.Selection = new PolygonSelection(
                new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Top),
                new SKPoint(r.Right, r.Bottom), new SKPoint(r.Left, r.Bottom));
            return;
        }

        if (_isMovingFloating && ctx.Document.FloatingPickup is { } fp)
        {
            EnsureLazyErase(ctx.Document, fp);
            var dx = position.X - _lastMove.X;
            var dy = position.Y - _lastMove.Y;
            fp.X += dx; fp.Y += dy;
            if (fp.Quad is { } q)
            {
                for (int i = 0; i < 4; i++)
                    q[i] = new SKPoint(q[i].X + dx, q[i].Y + dy);
            }
            _lastMove = position;
        }
    }

    public void OnPointerUp(SKPoint position, ToolContext ctx)
    {
        if (_isCreating)
        {
            if (ctx.Document.Selection is PolygonSelection p)
            {
                var bb = p.BoundingBox;
                if (bb.Width < 4 || bb.Height < 4) ctx.Document.Selection = null;
            }
            _isCreating = false;
        }
        _isMovingFloating = false;
        ctx.IsDrawing = false;
    }

    /// <summary>
    /// Antipattern §6: on first translate/scale/rotate of a quad pickup, lazily erase
    /// the active layer along the CURRENT polygon shape (not its bbox).
    /// </summary>
    private static void EnsureLazyErase(Document doc, FloatingPickup fp)
    {
        if (fp.OriginalAreaErased) return;
        if (doc.ActiveLayer is not PixelLayer pl) return;
        if (fp.Quad is not { } quad) return;

        // Snapshot the original quad — this is the shape we erase, even if the user
        // later moves corners further (those further moves are part of the pickup's
        // *current* shape, but the source region on the active layer stays this snapshot).
        fp.OriginalQuad = (SKPoint[])quad.Clone();

        using var canvas = new SKCanvas(pl.Bitmap);
        using var paint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var path = new SKPath();
        path.MoveTo(quad[0]);
        path.LineTo(quad[1]);
        path.LineTo(quad[2]);
        path.LineTo(quad[3]);
        path.Close();
        canvas.DrawPath(path, paint);
        fp.OriginalAreaErased = true;
    }
}
