using System.Windows.Input;
using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using SkiaSharp;

namespace PaintPro.Tools;

/// <summary>
/// Simple text tool: click → prompts caller for text via <see cref="TextRequested"/>.
/// Host (MainViewModel) shows a small input box (or system InputBox) and then calls
/// <see cref="CommitText"/> with the result. This keeps the tool free of any UI dependencies.
/// </summary>
public sealed class TextTool : ITool
{
    public string Name => "Text";
    public SKBitmap? PreviewBitmap => null;
    public Cursor? GetCursor(SKPoint position) => Cursors.IBeam;

    /// <summary>Raised when the user clicks the canvas. Host should respond with <see cref="CommitText"/>.</summary>
    public event Action<SKPoint>? TextRequested;

    private SKPoint _lastClick;
    private ToolContext? _ctx;

    /// <summary>
    /// Слой, по которому кликнули. Между кликом и ответом в окне ввода проходит сколько
    /// угодно времени, и активный слой за это время меняют свободно - а текст обязан
    /// лечь туда, куда пользователь целился.
    /// </summary>
    private Guid _targetLayerId;

    public void OnActivate(ToolContext ctx) { }
    public void OnDeactivate(ToolContext ctx) { }

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        // Спрашивать текст, которому некуда лечь, незачем: скрытый слой отсеиваем до диалога.
        if (ctx.DrawTarget() is not { } pl) return;
        _targetLayerId = pl.Id;
        _lastClick = position;
        _ctx = ctx;
        TextRequested?.Invoke(position);
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx) { }
    public void OnPointerUp(SKPoint position, ToolContext ctx) { }

    /// <summary>
    /// Called by the host after the user types something.
    ///
    /// Текст разбирается на строки: <see cref="SKCanvas.DrawText(string, float, float, SKPaint)"/>
    /// рисует ОДИН ряд глифов и перевода строки не знает - «\n» уходил в шрифт наравне с
    /// буквами и выходил прямоугольником-заглушкой, а всё, что за ним, ложилось той же
    /// строкой дальше вправо. Многострочное в поле попадает вставкой из буфера обмена, и
    /// пользователь получал вместо двух строк одну с квадратиком посередине.
    /// </summary>
    public void CommitText(string text, float fontSize = 24f, string family = "Segoe UI")
    {
        // Из одних пробелов и переводов строки чернил не выходит: запись в истории при
        // пустом холсте - это «изменено» на ровном месте и вопрос про сохранение после
        // ничего. Той же проверкой отсеивает пустую работу заливка.
        if (_ctx is null || string.IsNullOrWhiteSpace(text)) return;
        // Слой ищем по идентификатору, снятому на клике. Его могли и удалить, пока окно
        // ввода было открыто, - тогда класть текст некуда.
        if (_ctx.Document.FindPixelLayer(_targetLayerId) is not { } pl) return;
        // Text is rendered opaque and composited at the tool opacity, same as strokes.
        // Прозрачность в ноль - те же чернила, что и пустая строка: на холсте не остаётся
        // ничего, а запись в истории осталась бы.
        var alpha = (byte)(255 * Math.Clamp(_ctx.Opacity, 0f, 1f));
        if (alpha == 0) { _ctx.ReportTransparentInk(); return; }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        using var typeface = SKTypeface.FromFamilyName(family);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = _ctx.PrimaryColor.WithAlpha(255),
            TextSize = fontSize,
            Typeface = typeface,
        };
        // Межстрочный интервал берём у самого шрифта, а не выдумываем: у разных гарнитур
        // высота строки разная, а строки обязаны стоять ровно.
        float lineHeight = paint.FontSpacing;

        // Габарит - объединение габаритов всех строк, каждый на своей высоте.
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            var b = new SKRect();
            paint.MeasureText(lines[i], ref b);
            float dy = i * lineHeight;
            minX = MathF.Min(minX, b.Left);   maxX = MathF.Max(maxX, b.Right);
            minY = MathF.Min(minY, b.Top + dy); maxY = MathF.Max(maxY, b.Bottom + dy);
        }
        if (minX > maxX || minY > maxY) return;   // одни пробелы: рисовать нечего

        var pad = fontSize * 0.5f;
        var canvasRect = new SKRectI(
            (int)MathF.Floor(_lastClick.X + minX - pad),
            (int)MathF.Floor(_lastClick.Y + minY - pad),
            (int)MathF.Ceiling(_lastClick.X + maxX + pad),
            (int)MathF.Ceiling(_lastClick.Y + maxY + pad));
        canvasRect = SKRectI.Intersect(canvasRect, new SKRectI(0, 0, pl.Width, pl.Height));
        if (!canvasRect.HasArea()) return;

        var bmp = new SKBitmap(canvasRect.Width, canvasRect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.Transparent);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                c.DrawText(lines[i],
                    _lastClick.X - canvasRect.Left,
                    _lastClick.Y - canvasRect.Top + i * lineHeight,
                    paint);
            }
        }
        _ctx.History.ExecuteAndPush(
            new DrawStrokeCommand(bmp, canvasRect, SKBlendMode.SrcOver, alpha, pl.Id), _ctx.Document);
    }
}
