using System.Windows.Input;
using PaintPro.Commands;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Tools;

/// <summary>Flood fill at the click point with the primary colour.</summary>
public sealed class FillTool : ITool
{
    public string Name => "Fill";
    public SKBitmap? PreviewBitmap => null;
    public Cursor? GetCursor(SKPoint position) => Cursors.Cross;

    public void OnActivate(ToolContext ctx) { }
    public void OnDeactivate(ToolContext ctx) { }

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        if (ctx.DrawTarget() is null) return;
        var seed = new SKPointI((int)position.X, (int)position.Y);
        var color = ctx.PrimaryColor.WithAlpha((byte)(255 * ctx.Opacity));
        var cmd = new FillCommand(seed, color);
        // Не ExecuteAndPush: заливка бывает пустой (кликнули по уже залитому этим цветом
        // или мимо холста), а запись в историю нужна только если пиксели изменились.
        cmd.Execute(ctx.Document);
        if (cmd.ChangedAnything) ctx.History.Push(cmd);
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx) { }
    public void OnPointerUp(SKPoint position, ToolContext ctx) { }
}
