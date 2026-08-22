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
            // Со скрытого слоя поднимать нечего: пикап рисуется поверх документа всегда,
            // и пиксели невидимого слоя всплывали на экране, а после прижатия исчезали
            // обратно. Причину отказа объяснит сам DrawTarget.
            if (ctx.DrawTarget() is null) return;
            Services.PickupOps.PromoteRect(doc, rs.Rect);
            if (doc.FloatingPickup is not null)
            {
                _isMovingFloating = true;
                _lastMovePoint = position;
                ctx.IsDrawing = true;
                return;
            }
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
            Services.PickupOps.EnsureLazyErase(ctx.Document, fp);
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

}
