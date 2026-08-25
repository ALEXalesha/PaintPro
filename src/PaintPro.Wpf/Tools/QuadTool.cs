using System.Windows.Input;
using PaintPro.Models;
using PaintPro.Services;
using SkiaSharp;

namespace PaintPro.Tools;

/// <summary>
/// 4-point polygon selection. Initial drag creates an axis-aligned rect (4 corners),
/// then individual corners can be dragged independently to reshape the clip mask.
/// Content is NOT warped; the polygon acts as the clip path when committing/rendering.
///
/// Click inside the polygon lifts its pixels into a floating pickup, which then moves,
/// scales and rotates like a rectangular one. On the FIRST translate/scale/rotate (not
/// corner-drag) the source layer is erased using the CURRENT polygon shape, not its bbox
/// — see antipattern §6.
/// </summary>
public sealed class QuadTool : ITool
{
    public string Name => "Quad";
    public SKBitmap? PreviewBitmap => null;
    public Cursor? GetCursor(SKPoint position) => Cursors.Cross;

    /// <summary>
    /// Зона хвата угловой точки, в ЭКРАННЫХ пикселях: сама точка рисуется 9 экранных
    /// пикселей шириной (CanvasView.AddQuadCornerDot), и хват обязан примерно совпадать с
    /// тем, что видно. В координаты документа переводится по текущему масштабу.
    /// </summary>
    private const float CornerRadiusScreen = 10f;

    private SKPoint _origin;
    private bool _isCreating;
    private bool _isMovingFloating;
    private int _draggingCorner = -1;
    private SKPoint _lastMove;

    public void OnActivate(ToolContext ctx) { }

    public void OnDeactivate(ToolContext ctx)
    {
        // Прижимает поднятое ViewModel, а не инструмент - см. SelectTool.OnDeactivate.
        // Смена инструмента посреди жеста - см. SelectTool.OnDeactivate.
        _isCreating = false;
        _isMovingFloating = false;
        _draggingCorner = -1;
        ctx.IsDrawing = false;
    }

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        var doc = ctx.Document;

        if (doc.FloatingPickup is { } fp)
        {
            // Corner handles win over the body: they sit on the outline, which is inside
            // the bbox, so testing them first is what makes them reachable at all.
            // Углы хранятся неповёрнутыми, а нарисованы повёрнутыми: мышь приводим к
            // системе координат пикапа, иначе хват попадает мимо видимой точки.
            int pickupCorner = fp.Quad is { } quad
                ? PickupOps.HitCorner(quad, PickupOps.ToLocal(fp, position), ctx.ToDocument(CornerRadiusScreen))
                : -1;
            if (pickupCorner >= 0)
            {
                _draggingCorner = pickupCorner;
                ctx.IsDrawing = true;
                return;
            }
            if (PickupOps.HitBody(fp, position))
            {
                _isMovingFloating = true;
                _lastMove = position;
                ctx.IsDrawing = true;
                return;
            }
            doc.CommitFloating();
        }

        if (doc.Selection is PolygonSelection ps)
        {
            var corner = PickupOps.HitCorner(ps.Corners, position, ctx.ToDocument(CornerRadiusScreen));
            if (corner >= 0)
            {
                _draggingCorner = corner;
                ctx.IsDrawing = true;
                return;
            }
            if (ps.Contains(position))
            {
                // Со скрытого слоя поднимать нечего - см. SelectTool.
                if (ctx.DrawTarget() is null) return;
                PickupOps.PromoteQuad(doc, ps.Corners);
                if (doc.FloatingPickup is not null)
                {
                    _isMovingFloating = true;
                    _lastMove = position;
                    ctx.IsDrawing = true;
                    return;
                }
            }
        }

        _origin = position;
        _isCreating = true;
        doc.Selection = new PolygonSelection(position, position, position, position);
        ctx.IsDrawing = true;
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx)
    {
        var doc = ctx.Document;

        if (_isCreating)
        {
            var r = new SKRect(
                MathF.Min(_origin.X, position.X), MathF.Min(_origin.Y, position.Y),
                MathF.Max(_origin.X, position.X), MathF.Max(_origin.Y, position.Y));
            doc.Selection = new PolygonSelection(
                new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Top),
                new SKPoint(r.Right, r.Bottom), new SKPoint(r.Left, r.Bottom));
            return;
        }

        if (_draggingCorner >= 0)
        {
            // Reshaping the clip is not a transform, so it deliberately does NOT trigger
            // the lazy erase — the source pixels stay put until the pickup actually moves.
            if (doc.FloatingPickup is { Quad: { } quad } lifted)
            {
                quad[_draggingCorner] = PickupOps.ToLocal(lifted, position);
                doc.NotifyFloatingChanged();
            }
            else if (doc.Selection is PolygonSelection ps)
            {
                ps.SetCorner(_draggingCorner, position);
                doc.NotifySelectionChanged();
            }
            return;
        }

        if (_isMovingFloating && doc.FloatingPickup is { } fp)
        {
            PickupOps.EnsureLazyErase(doc, fp);
            PickupOps.Translate(fp, position.X - _lastMove.X, position.Y - _lastMove.Y);
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
                if (bb.Width < 4 || bb.Height < 4)
                {
                    ctx.Document.Selection = null;
                    // Молчащий отказ - см. SelectTool.OnPointerUp. Чистый клик не считается.
                    if (bb.Width >= 1 || bb.Height >= 1)
                        ctx.ReportHint?.Invoke(SelectTool.MinSelectionHint);
                }
            }
            _isCreating = false;
        }
        _isMovingFloating = false;
        _draggingCorner = -1;
        ctx.IsDrawing = false;
    }
}
