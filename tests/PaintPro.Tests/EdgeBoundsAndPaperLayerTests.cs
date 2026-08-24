using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Габарит без площади и нижний слой как бумага документа.
///
/// Прямоугольник нулевой ширины не пустой с точки зрения <see cref="SKRectI.IsEmpty"/> -
/// пустой для него только тот, у которого все четыре числа нули. Такие полосы выходят из
/// любого пересечения с холстом, когда фигура приткнулась к самому его краю, и дальше из
/// них строился битмап нулевого размера, на котором Skia падает.
/// </summary>
public class EdgeBoundsAndPaperLayerTests
{
    private static void FillRect(PixelLayer layer, SKRect rect, SKColor color)
    {
        using var c = new SKCanvas(layer.Bitmap);
        using var p = new SKPaint { Color = color, BlendMode = SKBlendMode.Src };
        c.DrawRect(rect, p);
    }

    // ───────── габарит без площади ─────────

    [Fact]
    public void A_zero_width_rectangle_is_not_considered_empty_by_skia()
    {
        // Ради этой проверки и написан HasArea: на ней держатся все остальные тесты файла.
        Assert.False(new SKRectI(10, 5, 10, 25).IsEmpty);
        Assert.False(new SKRectI(10, 5, 10, 25).HasArea());
        Assert.True(new SKRectI(10, 5, 30, 25).HasArea());
    }

    [Fact]
    public void An_intersection_that_only_touches_the_canvas_edge_has_no_area()
    {
        // Рамка, растянутая от верхнего края холста вверх: на холст приходится полоса
        // нулевой высоты, и IsEmpty про неё говорит «непустая».
        var strip = SKRectI.Intersect(new SKRectI(10, -30, 30, 0), new SKRectI(0, 0, 60, 60));
        Assert.False(strip.IsEmpty);
        Assert.False(strip.HasArea());
    }

    [Fact]
    public void Lifting_a_selection_that_only_touches_the_canvas_edge_is_refused()
    {
        var doc = new Document(60, 60);

        // Раньше здесь строился SKBitmap нулевой ширины, и приложение показывало
        // «Что-то пошло не так»: кеинг фона зовёт SKImage.FromBitmap, а тот на битмапе
        // без площади возвращает null.
        PickupOps.PromoteRect(doc, new SKRect(10, -30, 30, 0));

        Assert.Null(doc.FloatingPickup);
    }

    [Fact]
    public void Extracting_a_region_without_area_gives_a_usable_bitmap()
    {
        var layer = new PixelLayer(20, 20, SKColors.White);
        using var bmp = layer.ExtractRegion(new SKRectI(5, 5, 5, 15));
        Assert.True(bmp.Width > 0 && bmp.Height > 0);
    }

    [Fact]
    public void An_erase_command_is_not_built_for_a_selection_squeezed_to_a_line()
    {
        var vm = new MainViewModel();
        // Рамка целиком слева от холста, правым краем ровно по нулю.
        vm.Document.Selection = new RectSelection(new SKRect(-40, 10, 0, 40));

        vm.DeleteSelectionCommand.Execute(null);

        Assert.Empty(vm.Document.History.Commands);
    }

    [Fact]
    public void Copying_a_selection_squeezed_to_a_line_reports_that_there_is_nothing()
    {
        var doc = new Document(60, 60);
        doc.Selection = new RectSelection(new SKRect(-40, 10, 0, 40));
        // Рамка целиком за холстом: копировать нечего, и сказать об этом надо честно.
        // Прежде на такое возвращался битмап 1x1 - он уходил в буфер обмена наравне с
        // настоящей копией, затирая то, что там лежало.
        Assert.Null(ClipboardService.ExtractForClipboard(doc));
    }

    [Fact]
    public void A_stroke_left_entirely_outside_the_canvas_records_nothing()
    {
        var doc = new Document(60, 60);
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Black, ToolSize = 2f, Opacity = 1f };
        var brush = new BrushTool();
        brush.OnPointerDown(new SKPoint(-40, 30), ctx);
        brush.OnPointerMove(new SKPoint(-38, 30), ctx);
        brush.OnPointerUp(new SKPoint(-38, 30), ctx);

        Assert.Empty(doc.History.Commands);
    }

    // ───────── нижний слой - бумага ─────────

    [Fact]
    public void The_paper_layer_cannot_be_deleted_from_the_panel()
    {
        var vm = new MainViewModel();
        vm.AddLayerCommand.Execute(null);

        vm.RemoveLayerCommand.Execute(vm.LayerItems[0]);

        // Весь остальной код исходит из того, что нижний слой - бумага: ластик красит по
        // нему белым, смена размера холста заливает новую площадь белым, подъём выделения
        // выкусывает из него фон. Пока бумагу можно было удалить, ею молча становился
        // следующий слой - со всеми этими правилами разом.
        Assert.Equal(2, vm.Document.Layers.Count);
        Assert.NotEqual("", vm.StatusHint);
    }

    [Fact]
    public void A_layer_above_the_paper_still_deletes()
    {
        var vm = new MainViewModel();
        vm.AddLayerCommand.Execute(null);

        vm.RemoveLayerCommand.Execute(vm.LayerItems[1]);

        Assert.Single(vm.Document.Layers);
    }

    // ───────── «Создать» ─────────

    [Fact]
    public void A_new_document_keeps_only_the_paper_layer()
    {
        var doc = new Document(40, 40);
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Layer 1"), doc);
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Layer 2"), doc);

        doc.History.ExecuteAndPush(new ClearCanvasCommand(), doc);

        // Новый документ - чистый лист, а не старый со стёртым рисунком: прежде панель
        // слоёв после «Создать» показывала всю прежнюю стопку, а следующий «Добавить
        // слой» получал имя «Layer 3».
        Assert.Single(doc.Layers);
        Assert.True(((PixelLayer)doc.Layers[0]).IsAllWhite());
        Assert.Equal(0, doc.ActiveLayerIndex);
    }

    [Fact]
    public void Undoing_a_new_document_brings_the_whole_stack_back_with_its_identities()
    {
        var doc = new Document(40, 40);
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Layer 1"), doc);
        var addedId = doc.Layers[1].Id;
        doc.Layers[1].Opacity = 0.5f;
        FillRect((PixelLayer)doc.Layers[1], new SKRect(5, 5, 20, 20), SKColors.Lime);

        doc.History.ExecuteAndPush(new ClearCanvasCommand(), doc);
        doc.History.Undo(doc);

        // Идентификатор обязан быть прежним: по нему находят свою цель записи истории,
        // сделанные до очистки.
        Assert.Equal(2, doc.Layers.Count);
        Assert.Equal(addedId, doc.Layers[1].Id);
        Assert.Equal("Layer 1", doc.Layers[1].Name);
        Assert.Equal(0.5f, doc.Layers[1].Opacity, 3);
        Assert.Equal(SKColors.Lime, ((PixelLayer)doc.Layers[1]).Bitmap.GetPixel(10, 10));
    }

    [Fact]
    public void A_new_document_and_the_layer_that_created_it_undo_in_order()
    {
        var doc = new Document(40, 40);
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Layer 1"), doc);
        doc.History.ExecuteAndPush(new ClearCanvasCommand(), doc);

        doc.History.Undo(doc);   // вернулась стопка
        Assert.Equal(2, doc.Layers.Count);
        doc.History.Undo(doc);   // отменилось добавление слоя
        Assert.Single(doc.Layers);
    }

    [Fact]
    public void Redoing_a_new_document_blanks_it_again()
    {
        var doc = new Document(40, 40);
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Layer 1"), doc);
        FillRect((PixelLayer)doc.Layers[0], new SKRect(5, 5, 20, 20), SKColors.Red);

        doc.History.ExecuteAndPush(new ClearCanvasCommand(), doc);
        doc.History.Undo(doc);
        doc.History.Redo(doc);

        Assert.Single(doc.Layers);
        Assert.True(((PixelLayer)doc.Layers[0]).IsAllWhite());
    }
}
