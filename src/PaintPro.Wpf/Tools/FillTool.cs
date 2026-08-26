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
        // Floor, а не приведение к int: оно отбрасывает дробную часть В СТОРОНУ НУЛЯ, и
        // всё от -0.99 до 0 схлопывается в ноль. Точка чуть левее или выше холста
        // становилась левым верхним пикселем, и клик мимо читался как клик по углу.
        var seed = new SKPointI((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y));
        var alpha = (byte)(255 * Math.Clamp(ctx.Opacity, 0f, 1f));
        // Прозрачность в ноль - заливка, которой не было: смешивание с нулевой альфой не
        // меняет ни одного пикселя. Про это и раньше не появлялось записи в ленте, но и
        // слова пользователю не говорилось: он щёлкал по холсту, ничего не происходило, и
        // отличить это от сломанной программы было нельзя. Кисть, фигура и текст про свои
        // пустые чернила сообщают с 1.19.0 (ToolContext.ReportTransparentInk), заливка -
        // единственная из рисующих, кто молчал.
        if (alpha == 0) { ctx.ReportTransparentInk(); return; }
        var color = ctx.PrimaryColor.WithAlpha(alpha);
        var cmd = new FillCommand(seed, color);
        // Не ExecuteAndPush: заливка бывает пустой (кликнули по уже залитому этим цветом
        // или мимо холста), а запись в историю нужна только если пиксели изменились.
        cmd.Execute(ctx.Document);
        if (cmd.ChangedAnything) ctx.History.Push(cmd);
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx) { }
    public void OnPointerUp(SKPoint position, ToolContext ctx) { }
}
