using System.Windows.Input;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Tools;

/// <summary>Pan tool — drag the viewport. The actual pan is reported via the event for the host to apply.</summary>
public sealed class HandTool : ITool
{
    public string Name => "Hand";
    public SKBitmap? PreviewBitmap => null;
    public Cursor? GetCursor(SKPoint position) => Cursors.Hand;

    public event Action<SKPoint>? Panned;
    private SKPoint _last;
    private bool _panning;

    public void OnActivate(ToolContext ctx) { }
    public void OnDeactivate(ToolContext ctx) { _panning = false; }

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        _panning = true;
        _last = position;
        ctx.IsDrawing = true;
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx)
    {
        if (!_panning) return;
        var delta = new SKPoint(position.X - _last.X, position.Y - _last.Y);
        Panned?.Invoke(delta);
        _last = position;
    }

    public void OnPointerUp(SKPoint position, ToolContext ctx)
    {
        _panning = false;
        ctx.IsDrawing = false;
    }
}
