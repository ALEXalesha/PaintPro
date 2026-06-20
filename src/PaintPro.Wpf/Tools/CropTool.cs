using System.Windows.Input;
using PaintPro.Commands;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Tools;

/// <summary>
/// Crop tool — drag-rectangle then Enter/double-click commits the crop.
/// For v1 we crop immediately on PointerUp (no commit phase): drag → release → done.
/// The crop runs as a ResizeCanvasCommand wrapped with an offset so undo works correctly.
/// </summary>
public sealed class CropTool : ITool
{
    public string Name => "Crop";
    public SKBitmap? PreviewBitmap => null;
    public Cursor? GetCursor(SKPoint position) => Cursors.Cross;

    private SKPoint _origin;
    private bool _dragging;

    public void OnActivate(ToolContext ctx) { }
    public void OnDeactivate(ToolContext ctx) { }

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        _origin = position;
        _dragging = true;
        ctx.Document.EnterTransientMode(DocumentMode.Cropping);
        ctx.Document.Selection = new RectSelection(position.X, position.Y, 0, 0);
        ctx.IsDrawing = true;
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx)
    {
        if (!_dragging) return;
        var r = new SKRect(
            MathF.Min(_origin.X, position.X), MathF.Min(_origin.Y, position.Y),
            MathF.Max(_origin.X, position.X), MathF.Max(_origin.Y, position.Y));
        ctx.Document.Selection = new RectSelection(r);
    }

    public void OnPointerUp(SKPoint position, ToolContext ctx)
    {
        _dragging = false;
        ctx.IsDrawing = false;
        if (ctx.Document.Selection is not RectSelection rs) { ctx.Document.EnterTransientMode(DocumentMode.Idle); return; }
        var r = rs.Rect;
        if (r.Width < 4 || r.Height < 4)
        {
            ctx.Document.Selection = null;
            ctx.Document.EnterTransientMode(DocumentMode.Idle);
            return;
        }

        var cmd = new CropCommand(new SKRectI((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom));
        ctx.History.ExecuteAndPush(cmd, ctx.Document);
        ctx.Document.Selection = null;
        ctx.Document.EnterTransientMode(DocumentMode.Idle);
    }
}

/// <summary>Crop the active layer (and resize the canvas) to <paramref name="region"/>.</summary>
public sealed class CropCommand : IDocumentCommand
{
    private readonly SKRectI _region;
    private int _prevW, _prevH;
    private SKBitmap? _prevBitmap;

    public CropCommand(SKRectI region) => _region = region;
    public string DisplayName => "Crop";

    public void Execute(Document doc)
    {
        if (doc.ActiveLayer is not PixelLayer pl) return;
        _prevW = doc.CanvasWidth;
        _prevH = doc.CanvasHeight;
        _prevBitmap = pl.ExtractRegion(new SKRectI(0, 0, pl.Width, pl.Height));

        var r = SKRectI.Intersect(_region, new SKRectI(0, 0, pl.Width, pl.Height));
        var cropped = pl.ExtractRegion(r);

        var newLayer = new PixelLayer(r.Width, r.Height, SKColors.White)
        {
            Name = pl.Name, Visible = pl.Visible, Opacity = pl.Opacity,
        };
        using (var c = new SKCanvas(newLayer.Bitmap)) c.DrawBitmap(cropped, 0, 0);

        var idx = doc.Layers.IndexOf(pl);
        pl.Dispose();
        doc.Layers[idx] = newLayer;
        doc.CanvasWidth = r.Width;
        doc.CanvasHeight = r.Height;
    }

    public void Undo(Document doc)
    {
        if (doc.ActiveLayer is not PixelLayer pl || _prevBitmap is null) return;
        var restored = new PixelLayer(_prevW, _prevH, SKColors.White)
        {
            Name = pl.Name, Visible = pl.Visible, Opacity = pl.Opacity,
        };
        using (var c = new SKCanvas(restored.Bitmap)) c.DrawBitmap(_prevBitmap, 0, 0);
        var idx = doc.Layers.IndexOf(pl);
        pl.Dispose();
        doc.Layers[idx] = restored;
        doc.CanvasWidth = _prevW;
        doc.CanvasHeight = _prevH;
    }
}
