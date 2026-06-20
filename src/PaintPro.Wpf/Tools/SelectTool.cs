using System.Windows.Input;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Tools;

/// <summary>
/// Rectangular selection. On drag → creates a RectSelection.
/// Click inside an existing selection → promotes pixels into a FloatingPickup
/// and the next drag moves them. ESC commits / Delete clears.
/// </summary>
public sealed class SelectTool : ITool
{
    public string Name => "Select";
    public SKBitmap? PreviewBitmap => null;
    public Cursor? GetCursor(SKPoint position) => Cursors.Cross;

    private SKPoint _origin;
    private bool _isCreatingRect;
    private bool _isMovingFloating;
    private SKPoint _lastMovePoint;

    public void OnActivate(ToolContext ctx) { }
    public void OnDeactivate(ToolContext ctx)
    {
        // Commit any floating pickup so we don't leave half-state behind.
        ctx.Document.CommitFloating();
    }

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        var doc = ctx.Document;

        // Click inside floating bbox → move it.
        if (doc.FloatingPickup is { } fp && fp.CurrentBBox.Contains(position))
        {
            _isMovingFloating = true;
            _lastMovePoint = position;
            ctx.IsDrawing = true;
            return;
        }

        // Click outside floating → commit and start new selection.
        if (doc.FloatingPickup is not null)
        {
            doc.CommitFloating();
        }

        // Click inside existing rect selection → promote to floating pickup.
        if (doc.Selection is RectSelection rs && rs.Rect.Contains(position))
        {
            PromoteToFloating(doc, rs.Rect);
            _isMovingFloating = true;
            _lastMovePoint = position;
            ctx.IsDrawing = true;
            return;
        }

        // Start a new rectangle selection.
        _origin = position;
        _isCreatingRect = true;
        doc.Selection = new RectSelection(position.X, position.Y, 0, 0);
        ctx.IsDrawing = true;
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx)
    {
        if (_isCreatingRect)
        {
            var r = new SKRect(
                MathF.Min(_origin.X, position.X), MathF.Min(_origin.Y, position.Y),
                MathF.Max(_origin.X, position.X), MathF.Max(_origin.Y, position.Y));
            ctx.Document.Selection = new RectSelection(r);
            return;
        }

        if (_isMovingFloating && ctx.Document.FloatingPickup is { } fp)
        {
            var dx = position.X - _lastMovePoint.X;
            var dy = position.Y - _lastMovePoint.Y;
            EnsureLazyErase(ctx.Document, fp);
            fp.X += dx; fp.Y += dy;
            _lastMovePoint = position;
        }
    }

    public void OnPointerUp(SKPoint position, ToolContext ctx)
    {
        if (_isCreatingRect)
        {
            // Tiny rect (less than 4×4) → no selection.
            if (ctx.Document.Selection is RectSelection rs)
            {
                if (rs.Rect.Width < 4 || rs.Rect.Height < 4)
                    ctx.Document.Selection = null;
            }
            _isCreatingRect = false;
        }
        _isMovingFloating = false;
        ctx.IsDrawing = false;
    }

    private static void PromoteToFloating(Document doc, SKRect rect)
    {
        if (doc.ActiveLayer is not PixelLayer pl) return;
        var clamped = SKRectI.Intersect(SKRectI.Round(rect), new SKRectI(0, 0, pl.Width, pl.Height));
        if (clamped.IsEmpty) return;

        using var raw = pl.ExtractRegion(clamped);
        // Lift only the drawn marks: the white canvas background is keyed out so moving
        // the selection doesn't drag an opaque white box over whatever sits underneath.
        var pickupBitmap = Services.BitmapKeying.KeyOutBackground(raw, SKColors.White);
        var pickup = new FloatingPickup(pickupBitmap,
            new SKRect(clamped.Left, clamped.Top, clamped.Right, clamped.Bottom))
        {
            // Snapshot the layer as it is now (before any lazy-erase) so the eventual
            // commit can record an undoable before/after diff of the move.
            PreEditSnapshot = pl.ExtractRegion(new SKRectI(0, 0, pl.Width, pl.Height)),
        };
        doc.FloatingPickup = pickup;
    }

    /// <summary>
    /// Antipattern #6: erase the original area on the active layer at the FIRST move/scale/rotate.
    /// Implementation: track via FloatingPickup.OriginalAreaErased.
    /// </summary>
    private static void EnsureLazyErase(Document doc, FloatingPickup fp)
    {
        if (fp.OriginalAreaErased) return;
        if (doc.ActiveLayer is not PixelLayer pl) return;

        using var canvas = new SKCanvas(pl.Bitmap);
        using var paint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
        if (fp.Quad is { } quad)
        {
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
}
