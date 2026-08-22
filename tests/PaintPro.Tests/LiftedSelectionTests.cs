using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Поднятое выделение: удаление до первого перемещения, форма quad'а при коммите,
/// нажатие на ручку без перетаскивания и отмена вставки.
/// </summary>
public class LiftedSelectionTests
{
    private static (Document doc, PixelLayer layer, ToolContext ctx) MakeDoc(int size = 40)
    {
        var doc = new Document(size, size);
        var layer = (PixelLayer)doc.ActiveLayer;
        return (doc, layer, new ToolContext(doc) { PrimaryColor = SKColors.Black, Opacity = 1f });
    }

    private static void FillRect(PixelLayer layer, SKRect rect, SKColor color)
    {
        using var c = new SKCanvas(layer.Bitmap);
        using var p = new SKPaint { Color = color };
        c.DrawRect(rect, p);
    }

    // ───────── удаление поднятого, но не сдвинутого выделения ─────────

    [Fact]
    public void Deleting_a_lifted_selection_erases_it_even_before_the_first_move()
    {
        var (doc, layer, ctx) = MakeDoc();
        FillRect(layer, new SKRect(5, 5, 35, 35), SKColors.Red);
        doc.Selection = new RectSelection(10, 10, 20, 20);

        new SelectTool().OnPointerDown(new SKPoint(20, 20), ctx);   // подъём, но без движения
        Assert.NotNull(doc.FloatingPickup);

        doc.DiscardFloating();

        // Дыра на месте выделения: подъём пикселей не стирает, стирание идёт при первом
        // перемещении, и до этой правки Delete по только что поднятому просто возвращал
        // их обратно.
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(20, 20));
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(7, 7));    // за выделением - нетронуто
        Assert.Equal(1, doc.History.Cursor);                        // и это отменяемо
    }

    [Fact]
    public void Undoing_that_delete_brings_the_pixels_back()
    {
        var (doc, layer, ctx) = MakeDoc();
        FillRect(layer, new SKRect(5, 5, 35, 35), SKColors.Red);
        doc.Selection = new RectSelection(10, 10, 20, 20);
        new SelectTool().OnPointerDown(new SKPoint(20, 20), ctx);

        doc.DiscardFloating();
        doc.History.Undo(doc);

        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(20, 20));
    }

    // ───────── подъём без перемещения ─────────

    [Fact]
    public void Lifting_and_putting_back_without_moving_records_nothing()
    {
        var (doc, layer, _) = MakeDoc();
        FillRect(layer, new SKRect(5, 5, 35, 35), SKColors.Red);

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));
        var pickup = doc.FloatingPickup!;
        // Ровно то, что делало нажатие на ручку: исходная область стёрта, а пикап
        // никуда не сдвинулся.
        PickupOps.EnsureLazyErase(doc, pickup);

        Assert.False(pickup.HasMoved);

        doc.CommitFloating();

        Assert.Equal(0, doc.History.Cursor);                       // пустой записи нет
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(20, 20)); // холст вернулся как был
        Assert.Null(doc.FloatingPickup);
    }

    [Fact]
    public void A_pickup_that_did_not_move_is_not_unsaved_work()
    {
        var (doc, layer, _) = MakeDoc();
        FillRect(layer, new SKRect(5, 5, 35, 35), SKColors.Red);

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));
        PickupOps.EnsureLazyErase(doc, doc.FloatingPickup!);

        Assert.False(doc.FloatingPickup!.HasMoved);

        doc.FloatingPickup.X += 3;
        Assert.True(doc.FloatingPickup.HasMoved);
    }

    [Fact]
    public void A_moved_pickup_still_records_its_commit()
    {
        var (doc, layer, _) = MakeDoc();
        FillRect(layer, new SKRect(5, 5, 35, 35), SKColors.Red);

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 20, 20));
        var pickup = doc.FloatingPickup!;
        PickupOps.EnsureLazyErase(doc, pickup);
        pickup.X += 5;

        doc.CommitFloating();

        Assert.Equal(1, doc.History.Cursor);
    }

    // ───────── quad: угол сдвинули, тело нет ─────────

    [Fact]
    public void Reshaping_a_lifted_quad_erases_the_original_polygon_on_commit()
    {
        var (doc, layer, ctx) = MakeDoc();
        FillRect(layer, new SKRect(5, 5, 35, 35), SKColors.Red);
        doc.Selection = new PolygonSelection(
            new SKPoint(10, 10), new SKPoint(30, 10),
            new SKPoint(30, 30), new SKPoint(10, 30));

        var tool = new QuadTool();
        tool.OnPointerDown(new SKPoint(20, 20), ctx);   // подъём
        tool.OnPointerUp(new SKPoint(20, 20), ctx);

        tool.OnPointerDown(new SKPoint(10, 10), ctx);   // взялись за угол
        tool.OnPointerMove(new SKPoint(20, 10), ctx);   // и увели его вправо
        tool.OnPointerUp(new SKPoint(20, 10), ctx);

        doc.CommitFloating();

        // Форма стирается лениво, при первом перемещении; тело пикапа не двигали, и без
        // явного стирания исходный полигон оставался лежать под прижатым пикапом.
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(12, 12)); // отрезанный угол
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(25, 25));   // то, что осталось в маске
        Assert.Equal(1, doc.History.Cursor);                         // и это отменяемо
    }

    [Fact]
    public void Dragging_a_quad_corner_first_does_not_move_what_gets_cut_out()
    {
        var (doc, layer, ctx) = MakeDoc();
        FillRect(layer, new SKRect(0, 0, 40, 40), SKColors.Red);
        doc.Selection = new PolygonSelection(
            new SKPoint(10, 10), new SKPoint(30, 10),
            new SKPoint(30, 30), new SKPoint(10, 30));

        var tool = new QuadTool();
        tool.OnPointerDown(new SKPoint(20, 20), ctx);   // подъём формы 10..30
        tool.OnPointerUp(new SKPoint(20, 20), ctx);

        tool.OnPointerDown(new SKPoint(10, 10), ctx);   // угол увели НАРУЖУ выделения
        tool.OnPointerMove(new SKPoint(2, 2), ctx);
        tool.OnPointerUp(new SKPoint(2, 2), ctx);

        tool.OnPointerDown(new SKPoint(25, 25), ctx);   // и только теперь двинули тело
        tool.OnPointerMove(new SKPoint(28, 28), ctx);
        tool.OnPointerUp(new SKPoint(28, 28), ctx);

        // Из слоя выкусывается то, что подняли. Пока стирание шло по текущей форме,
        // перетаскивание угла подменяло её, и в слое пробивалась дыра там, где
        // пользователь ничего не выделял.
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(4, 4));
    }

    // ───────── отмена вставки ─────────

    [Fact]
    public void Undo_after_paste_takes_the_history_entry_with_it()
    {
        var (doc, _, _) = MakeDoc(20);
        using var src = new SKBitmap(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(src)) c.Clear(SKColors.Red);

        doc.History.ExecuteAndPush(new PasteCommand(src, new SKPoint(2, 2)), doc);
        Assert.Equal(1, doc.History.Cursor);
        Assert.NotNull(doc.FloatingPickup);

        doc.History.Undo(doc);

        // Пикап вставки создан командой, которая уже лежит в списке: снимать его отдельно
        // значило оставить запись «Вставка» текущей при пустом холсте, и повтор её уже
        // не возвращал - курсор-то не двигался.
        Assert.Null(doc.FloatingPickup);
        Assert.Equal(0, doc.History.Cursor);

        doc.History.Redo(doc);

        Assert.Equal(1, doc.History.Cursor);
        Assert.NotNull(doc.FloatingPickup);
    }

    [Fact]
    public void Undo_of_a_lifted_selection_still_only_drops_the_pickup()
    {
        var (doc, layer, _) = MakeDoc();
        FillRect(layer, new SKRect(5, 5, 35, 35), SKColors.Red);
        doc.History.ExecuteAndPush(new ClearCanvasCommand(SKColors.White), doc);
        FillRect(layer, new SKRect(5, 5, 35, 35), SKColors.Red);

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));
        PickupOps.EnsureLazyErase(doc, doc.FloatingPickup!);
        doc.FloatingPickup!.X += 5;

        doc.History.Undo(doc);

        // Подъём - правка, которой в списке ещё нет: первый Ctrl+Z снимает её, курсор
        // остаётся на месте.
        Assert.Null(doc.FloatingPickup);
        Assert.Equal(1, doc.History.Cursor);
    }
}
