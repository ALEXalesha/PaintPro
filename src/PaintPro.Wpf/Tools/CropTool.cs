using System.Windows.Input;
using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
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

    /// <summary>Почему кадрирование не сработало. Граница та же, что у выделения.</summary>
    private const string TooSmallHint =
        "Рамка кадрирования меньше 4 пикселей — обведите область побольше";

    private SKPoint _origin;
    private bool _dragging;

    /// <summary>
    /// Обведённая, но ещё не применённая рамка (1.28.0). Кадрирование, как в Electron-версии,
    /// ждёт подтверждения - «Обрезать» или Enter, «Отмена» или Escape: раньше оно срабатывало,
    /// едва отпускали мышь, и промах рамкой обрезал картинку.
    /// </summary>
    private SKRectI? _pending;
    public bool HasPending => _pending is not null;
    public event Action? PendingChanged;

    private void SetPending(SKRectI? value)
    {
        _pending = value;
        PendingChanged?.Invoke();
    }

    /// <summary>Применить обведённую рамку. False - применять нечего.</summary>
    public bool Apply(ToolContext ctx)
    {
        if (_pending is not { } region) return false;
        SetPending(null);
        // Прижимаем поднятый объект ДО кадрирования, как это делают поворот, отражение и
        // смена размера. После него прижимать некуда: команда заменяет содержимое всех
        // слоёв и меняет размер холста, и объект просто снимается вместе с рамкой -
        // пользователь терял то, что держал в руках, без записи в истории.
        ctx.Document.CommitFloating();
        var cmd = DocumentTransform.Crop(ctx.Document, region);
        ctx.History.ExecuteAndPush(cmd, ctx.Document);
        ctx.Document.Selection = null;
        ctx.Document.EnterTransientMode(DocumentMode.Idle);
        return true;
    }

    /// <summary>Убрать обведённую рамку, ничего не обрезая.</summary>
    public void Cancel(ToolContext ctx)
    {
        if (_pending is null) return;
        SetPending(null);
        ctx.Document.Selection = null;
        ctx.Document.EnterTransientMode(DocumentMode.Idle);
    }

    public void OnActivate(ToolContext ctx) { }

    public void OnDeactivate(ToolContext ctx)
    {
        // Другой инструмент - рамка без подтверждения отменяется, как в Electron-версии.
        Cancel(ctx);
        // Switching tools mid-drag has to unwind the transient mode by hand: nothing else
        // recomputes it while the document is in Cropping, so it would stay stuck there
        // and selection state would stop driving Mode at all.
        if (!_dragging) return;
        _dragging = false;
        ctx.IsDrawing = false;
        ctx.Document.Selection = null;
        ctx.Document.EnterTransientMode(DocumentMode.Idle);
    }

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        // Новая рамка вместо прежней, ещё не применённой.
        if (_pending is not null) SetPending(null);
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
            // Кадрирование не срабатывало молча: пользователь обводил рамку, отпускал, и
            // холст оставался прежним без единого слова о том, почему. Причина всегда одна
            // и та же - рамка меньше четырёх пикселей по стороне. Про чистый клик молчим:
            // им из режима кадрирования как раз выходят.
            if (r.Width >= 1 || r.Height >= 1) ctx.ReportHint?.Invoke(TooSmallHint);
            return;
        }

        var region = SKRectI.Intersect(
            new SKRectI((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom),
            new SKRectI(0, 0, ctx.Document.CanvasWidth, ctx.Document.CanvasHeight));
        if (!region.HasArea())
        {
            ctx.Document.Selection = null;
            ctx.Document.EnterTransientMode(DocumentMode.Idle);
            ctx.ReportHint?.Invoke("Рамка кадрирования целиком за пределами холста");
            return;
        }

        // Рамка остаётся на холсте и ждёт «Обрезать» (Enter) или «Отмена» (Escape).
        SetPending(region);
    }
}
