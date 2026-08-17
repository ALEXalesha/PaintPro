using System.Windows.Input;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Tools;

/// <summary>
/// Pan tool. The scrolling itself lives in CanvasView, which owns the ScrollViewer and
/// works in viewport coordinates.
///
/// This tool used to compute the drag delta in document coordinates and raise an event —
/// but nothing subscribed, so the Hand button did nothing at all. Document coordinates are
/// also the wrong frame for the job: scrolling moves the canvas under the cursor, so the
/// next delta measured that way partly cancels the previous scroll and the view oscillates.
/// Viewport coordinates do not move when the content scrolls, so CanvasView uses those.
/// </summary>
public sealed class HandTool : ITool
{
    public string Name => "Hand";
    public SKBitmap? PreviewBitmap => null;
    public Cursor? GetCursor(SKPoint position) => Cursors.Hand;

    public void OnActivate(ToolContext ctx) { }
    public void OnDeactivate(ToolContext ctx) { ctx.IsDrawing = false; }

    public void OnPointerDown(SKPoint position, ToolContext ctx) => ctx.IsDrawing = true;
    public void OnPointerMove(SKPoint position, ToolContext ctx) { }
    public void OnPointerUp(SKPoint position, ToolContext ctx) => ctx.IsDrawing = false;
}
