using System.Windows.Input;
using PaintPro.Commands;
using PaintPro.Models;
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

    public void OnActivate(ToolContext ctx) { }
    public void OnDeactivate(ToolContext ctx) { }

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        // Спрашивать текст, которому некуда лечь, незачем: скрытый слой отсеиваем до диалога.
        if (ctx.DrawTarget() is null) return;
        _lastClick = position;
        _ctx = ctx;
        TextRequested?.Invoke(position);
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx) { }
    public void OnPointerUp(SKPoint position, ToolContext ctx) { }

    /// <summary>Called by the host after the user types something.</summary>
    public void CommitText(string text, float fontSize = 24f, string family = "Segoe UI")
    {
        if (_ctx is null || string.IsNullOrEmpty(text)) return;
        if (_ctx.DrawTarget() is not { } pl) return;

        using var typeface = SKTypeface.FromFamilyName(family);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = _ctx.PrimaryColor.WithAlpha(255),
            TextSize = fontSize,
            Typeface = typeface,
        };
        // Measure bbox.
        var bounds = new SKRect();
        paint.MeasureText(text, ref bounds);
        var pad = fontSize * 0.5f;
        var canvasRect = new SKRectI(
            (int)MathF.Floor(_lastClick.X + bounds.Left - pad),
            (int)MathF.Floor(_lastClick.Y + bounds.Top  - pad),
            (int)MathF.Ceiling(_lastClick.X + bounds.Right  + pad),
            (int)MathF.Ceiling(_lastClick.Y + bounds.Bottom + pad));
        canvasRect = SKRectI.Intersect(canvasRect, new SKRectI(0, 0, pl.Width, pl.Height));
        if (canvasRect.IsEmpty) return;

        var bmp = new SKBitmap(canvasRect.Width, canvasRect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.Transparent);
            c.DrawText(text, _lastClick.X - canvasRect.Left, _lastClick.Y - canvasRect.Top, paint);
        }
        // Text is rendered opaque and composited at the tool opacity, same as strokes.
        var alpha = (byte)(255 * Math.Clamp(_ctx.Opacity, 0f, 1f));
        _ctx.History.ExecuteAndPush(
            new DrawStrokeCommand(bmp, canvasRect, SKBlendMode.SrcOver, alpha), _ctx.Document);
    }
}
