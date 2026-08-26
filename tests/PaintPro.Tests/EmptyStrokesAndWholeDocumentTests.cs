using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Жест, от которого на холсте не осталось ни одного изменённого пикселя, не попадает в
/// ленту истории и не делает документ изменённым.
///
/// Правило было доведено до половины: заливка и стирание выделения свою пустую работу
/// отсеивали, а штрих, фигура, текст и операции над всем документом - нет. Ластик по
/// нетронутой белой бумаге, белая кисть по белому, штрих мимо холста, кадрирование по
/// всему холсту, отражение симметричной картинки - каждый из них оставлял в панели
/// истории строку и включал признак несохранённой работы: приложение спрашивало про
/// сохранение после жеста, которого на экране не было видно.
///
/// Спрашивает теперь сама лента, одним и тем же вопросом ко всем
/// (<see cref="IDocumentCommand.ChangedAnything"/>).
/// </summary>
public class EmptyStrokesAndWholeDocumentTests
{
    private static ToolContext Ctx(Document d)
        => new(d) { PrimaryColor = SKColors.Black, ToolSize = 6f, Opacity = 1f };

    private static void Paint(Document doc, SKRect r, SKColor color)
    {
        using var c = new SKCanvas(((PixelLayer)doc.Layers[0]).Bitmap);
        c.DrawRect(r, new SKPaint { Color = color });
    }

    // ───────── штрих ─────────

    [Fact]
    public void An_eraser_over_untouched_paper_records_nothing()
    {
        var doc = new Document(60, 60);
        var ctx = Ctx(doc);
        var t = new EraserTool();
        t.OnPointerDown(new SKPoint(20, 20), ctx);
        t.OnPointerMove(new SKPoint(30, 30), ctx);
        t.OnPointerUp(new SKPoint(30, 30), ctx);

        Assert.Empty(doc.History.Commands);
    }

    [Fact]
    public void An_eraser_over_an_empty_upper_layer_records_nothing()
    {
        var doc = new Document(60, 60);
        doc.Layers.Add(new PixelLayer(60, 60, SKColors.Transparent));
        doc.ActiveLayerIndex = 1;
        var ctx = Ctx(doc);
        var t = new EraserTool();
        t.OnPointerDown(new SKPoint(20, 20), ctx);
        t.OnPointerMove(new SKPoint(40, 40), ctx);
        t.OnPointerUp(new SKPoint(40, 40), ctx);

        Assert.Empty(doc.History.Commands);
    }

    [Fact]
    public void A_white_brush_over_white_paper_records_nothing()
    {
        var doc = new Document(60, 60);
        var ctx = Ctx(doc);
        ctx.PrimaryColor = SKColors.White;
        var t = new BrushTool();
        t.OnPointerDown(new SKPoint(20, 20), ctx);
        t.OnPointerMove(new SKPoint(25, 25), ctx);
        t.OnPointerUp(new SKPoint(25, 25), ctx);

        Assert.Empty(doc.History.Commands);
    }

    [Fact]
    public void A_white_shape_over_white_paper_records_nothing()
    {
        var doc = new Document(60, 60);
        var ctx = Ctx(doc);
        ctx.PrimaryColor = SKColors.White;
        var t = new RectShapeTool { Fill = true };
        t.OnPointerDown(new SKPoint(10, 10), ctx);
        t.OnPointerMove(new SKPoint(40, 40), ctx);
        t.OnPointerUp(new SKPoint(40, 40), ctx);

        Assert.Empty(doc.History.Commands);
    }

    [Fact]
    public void White_text_over_white_paper_records_nothing()
    {
        var doc = new Document(160, 60);
        var ctx = Ctx(doc);
        ctx.PrimaryColor = SKColors.White;
        var t = new TextTool();
        t.OnPointerDown(new SKPoint(10, 40), ctx);
        t.CommitText("Привет");

        Assert.Empty(doc.History.Commands);
    }

    [Fact]
    public void A_stroke_entirely_off_the_canvas_records_nothing()
    {
        var doc = new Document(40, 40);
        var ctx = Ctx(doc);
        ctx.PrimaryColor = SKColors.Red;
        var t = new BrushTool();
        t.OnPointerDown(new SKPoint(-200, -200), ctx);
        t.OnPointerMove(new SKPoint(-190, -190), ctx);
        t.OnPointerUp(new SKPoint(-190, -190), ctx);

        Assert.Empty(doc.History.Commands);
    }

    /// <summary>
    /// Главное, ради чего правило и заводилось: после такого жеста приложение не спрашивает
    /// про сохранение.
    /// </summary>
    [Fact]
    public void An_empty_stroke_keeps_the_document_clean()
    {
        var vm = new MainViewModel();
        vm.Document.History.MarkSaved();

        var t = new EraserTool();
        t.OnPointerDown(new SKPoint(20, 20), vm.ToolContext);
        t.OnPointerMove(new SKPoint(30, 30), vm.ToolContext);
        t.OnPointerUp(new SKPoint(30, 30), vm.ToolContext);

        Assert.False(vm.IsDirty);
    }

    /// <summary>А настоящий штрих записывается как и раньше - правило не должно съесть работу.</summary>
    [Fact]
    public void A_stroke_that_does_change_pixels_is_still_recorded()
    {
        var doc = new Document(60, 60);
        var ctx = Ctx(doc);
        ctx.PrimaryColor = SKColors.Red;
        var t = new BrushTool();
        t.OnPointerDown(new SKPoint(20, 20), ctx);
        t.OnPointerMove(new SKPoint(30, 30), ctx);
        t.OnPointerUp(new SKPoint(30, 30), ctx);

        Assert.Single(doc.History.Commands);
    }

    [Fact]
    public void An_eraser_that_does_erase_something_is_still_recorded()
    {
        var doc = new Document(60, 60);
        Paint(doc, new SKRect(10, 10, 50, 50), SKColors.Red);
        var ctx = Ctx(doc);
        ctx.ToolSize = 10f;
        var t = new EraserTool();
        t.OnPointerDown(new SKPoint(30, 30), ctx);
        t.OnPointerUp(new SKPoint(30, 30), ctx);

        Assert.Single(doc.History.Commands);
    }

    // ───────── операции над всем документом ─────────

    [Fact]
    public void Cropping_to_the_whole_canvas_records_nothing()
    {
        var doc = new Document(40, 40);
        Paint(doc, new SKRect(5, 5, 35, 35), SKColors.Red);

        doc.History.ExecuteAndPush(DocumentTransform.Crop(doc, new SKRectI(0, 0, 40, 40)), doc);

        Assert.Empty(doc.History.Commands);
        Assert.Equal(40, doc.CanvasWidth);
    }

    [Fact]
    public void Flipping_a_symmetric_picture_records_nothing()
    {
        var doc = new Document(40, 40);
        Paint(doc, new SKRect(10, 5, 30, 35), SKColors.Red);   // симметрична по горизонтали

        doc.History.ExecuteAndPush(DocumentTransform.Flip(doc, horizontal: true), doc);

        Assert.Empty(doc.History.Commands);
    }

    [Fact]
    public void Flipping_an_asymmetric_picture_is_recorded()
    {
        var doc = new Document(40, 40);
        Paint(doc, new SKRect(2, 5, 12, 35), SKColors.Red);

        doc.History.ExecuteAndPush(DocumentTransform.Flip(doc, horizontal: true), doc);

        Assert.Single(doc.History.Commands);
    }

    [Fact]
    public void Rotating_a_blank_square_canvas_records_nothing()
    {
        var doc = new Document(40, 40);

        doc.History.ExecuteAndPush(DocumentTransform.Rotate(doc, MathF.PI / 2f), doc);

        Assert.Empty(doc.History.Commands);
    }

    /// <summary>
    /// А вот поворот НЕквадратного холста меняет сам холст - стороны меняются местами, -
    /// и записывается всегда, даже на чистом листе.
    /// </summary>
    [Fact]
    public void Rotating_a_blank_rectangular_canvas_is_recorded()
    {
        var doc = new Document(60, 30);

        doc.History.ExecuteAndPush(DocumentTransform.Rotate(doc, MathF.PI / 2f), doc);

        Assert.Single(doc.History.Commands);
        Assert.Equal(30, doc.CanvasWidth);
    }

    [Fact]
    public void Opening_a_picture_the_canvas_already_holds_records_nothing()
    {
        var doc = new Document(30, 30);
        using var image = new SKBitmap(30, 30, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(image)) c.Clear(SKColors.White);

        doc.History.ExecuteAndPush(DocumentTransform.OpenImage(doc, image), doc);

        Assert.Empty(doc.History.Commands);
    }

    /// <summary>
    /// Свойства слоёв считаются наравне с пикселями. Открытие в документ со спрятанной
    /// бумагой пикселей не меняет, а видимость - меняет, и такую запись пропускать нельзя:
    /// иначе отменить включение слоя было бы нечем.
    /// </summary>
    [Fact]
    public void Opening_the_same_picture_into_a_hidden_paper_is_recorded()
    {
        var doc = new Document(30, 30);
        doc.Layers[0].Visible = false;
        using var image = new SKBitmap(30, 30, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(image)) c.Clear(SKColors.White);

        doc.History.ExecuteAndPush(DocumentTransform.OpenImage(doc, image), doc);

        Assert.Single(doc.History.Commands);
        Assert.True(doc.Layers[0].Visible);
    }

    /// <summary>
    /// Отказ от записи не отменяет побочных действий команды: рамка после кадрирования
    /// снимается, даже если кадрировать было нечего. Иначе она осталась бы висеть.
    /// </summary>
    [Fact]
    public void A_skipped_crop_still_drops_the_selection()
    {
        var doc = new Document(40, 40);
        doc.Selection = new RectSelection(5, 5, 20, 20);

        doc.History.ExecuteAndPush(DocumentTransform.Crop(doc, new SKRectI(0, 0, 40, 40)), doc);

        Assert.Empty(doc.History.Commands);
        Assert.Null(doc.Selection);
    }
}
