using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Зона хвата угла, две руки для одного объекта, пустые правки и текст в несколько строк.
/// </summary>
public class GrabZoneAndEmptyEditTests
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
        using var p = new SKPaint { Color = color, BlendMode = SKBlendMode.Src };
        c.DrawRect(rect, p);
    }

    private static SKPoint[] Square(float left, float top, float side) => new[]
    {
        new SKPoint(left, top), new SKPoint(left + side, top),
        new SKPoint(left + side, top + side), new SKPoint(left, top + side),
    };

    // ───────── зона хвата угла не должна съедать саму фигуру ─────────

    [Fact]
    public void A_small_polygon_selection_can_still_be_lifted()
    {
        var (doc, layer, ctx) = MakeDoc();
        FillRect(layer, new SKRect(5, 5, 20, 20), SKColors.Red);
        doc.Selection = new PolygonSelection(
            new SKPoint(5, 5), new SKPoint(20, 5), new SKPoint(20, 20), new SKPoint(5, 20));

        // Клик ровно в середину выделения 15×15. Зона хвата - 10 экранных пикселей, а до
        // ближайшего угла отсюда 9.9: пока хват не знал о размере фигуры, четыре зоны
        // смыкались, клик читался как хват угла, и поднять такое выделение было нельзя.
        new QuadTool().OnPointerDown(new SKPoint(12, 12), ctx);

        Assert.NotNull(doc.FloatingPickup);
    }

    [Fact]
    public void A_small_lifted_quad_is_dragged_by_its_body_not_by_a_corner()
    {
        var (doc, layer, ctx) = MakeDoc();
        FillRect(layer, new SKRect(5, 5, 20, 20), SKColors.Red);
        PickupOps.PromoteQuad(doc, Square(5, 5, 15));

        var q = new QuadTool();
        q.OnPointerDown(new SKPoint(12, 12), ctx);
        q.OnPointerMove(new SKPoint(22, 12), ctx);

        // Уехать должен весь объект, а не один его угол.
        Assert.Equal(15f, doc.FloatingPickup!.X, 1);
        Assert.Equal(5f, doc.FloatingPickup!.Y, 1);
    }

    [Fact]
    public void A_corner_of_a_small_quad_is_still_grabbable()
    {
        var (doc, layer, ctx) = MakeDoc();
        FillRect(layer, new SKRect(5, 5, 20, 20), SKColors.Red);
        doc.Selection = new PolygonSelection(
            new SKPoint(5, 5), new SKPoint(20, 5), new SKPoint(20, 20), new SKPoint(5, 20));

        var q = new QuadTool();
        q.OnPointerDown(new SKPoint(6, 6), ctx);       // почти в самом углу
        q.OnPointerMove(new SKPoint(2, 2), ctx);

        Assert.Null(doc.FloatingPickup);               // это был хват угла, а не подъём
        var ps = Assert.IsType<PolygonSelection>(doc.Selection);
        Assert.Equal(2f, ps.Corners[0].X, 3);
    }

    // ───────── «Выделение» и «Четырёхугольник» - две руки для одного объекта ─────────

    [Fact]
    public void Switching_between_the_two_selection_tools_keeps_the_pickup()
    {
        var vm = new MainViewModel();
        vm.ActiveTool = ToolKind.Select;
        PickupOps.PromoteRect(vm.Document, new SKRect(10, 10, 40, 40));

        vm.ActiveTool = ToolKind.Quad;

        Assert.NotNull(vm.Document.FloatingPickup);
        Assert.Equal(0, vm.Document.History.Cursor);   // ничего не прижималось
    }

    [Fact]
    public void Switching_to_a_drawing_tool_still_lands_the_pickup()
    {
        var vm = new MainViewModel();
        var layer = (PixelLayer)vm.Document.Layers[0];
        FillRect(layer, new SKRect(10, 10, 40, 40), SKColors.Red);
        vm.ActiveTool = ToolKind.Select;
        PickupOps.PromoteRect(vm.Document, new SKRect(10, 10, 40, 40));
        PickupOps.EnsureLazyErase(vm.Document, vm.Document.FloatingPickup!);
        PickupOps.Translate(vm.Document.FloatingPickup!, 50, 0);

        vm.ActiveTool = ToolKind.Brush;

        Assert.Null(vm.Document.FloatingPickup);
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(70, 20));
    }

    [Fact]
    public void Deselect_lands_a_lifted_object_instead_of_leaving_it_hanging()
    {
        var vm = new MainViewModel();
        var layer = (PixelLayer)vm.Document.Layers[0];
        FillRect(layer, new SKRect(10, 10, 40, 40), SKColors.Red);
        PickupOps.PromoteRect(vm.Document, new SKRect(10, 10, 40, 40));
        PickupOps.EnsureLazyErase(vm.Document, vm.Document.FloatingPickup!);
        PickupOps.Translate(vm.Document.FloatingPickup!, 50, 0);

        vm.DeselectCommand.Execute(null);

        Assert.Null(vm.Document.FloatingPickup);
        Assert.Null(vm.Document.Selection);
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(70, 20));
    }

    // ───────── отмена стирания многоугольника возвращает и кромку сглаживания ─────────

    [Fact]
    public void Undoing_a_polygon_erase_at_fractional_coordinates_restores_every_pixel()
    {
        var (doc, layer, _) = MakeDoc();
        FillRect(layer, new SKRect(0, 0, 40, 40), SKColors.Red);
        var poly = new[]
        {
            new SKPoint(10.6f, 10.6f), new SKPoint(30.4f, 10.6f),
            new SKPoint(30.4f, 30.4f), new SKPoint(10.6f, 30.4f),
        };
        var bounds = SKRectI.Round(new SKRect(10.6f, 10.6f, 30.4f, 30.4f));

        doc.History.ExecuteAndPush(new EraseRegionCommand(bounds, poly, SKColors.White), doc);
        doc.History.Undo(doc);

        // Контур сглажен и задевает пиксели за округлённым габаритом. Пока снимок брался
        // ровно по нему, после Ctrl+Z по краю оставалась бледная кайма.
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 40; x++)
                Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(x, y));
    }

    // ───────── правки, которые ничего не меняют, не попадают в историю ─────────

    [Fact]
    public void A_stroke_at_zero_opacity_records_nothing()
    {
        var (doc, layer, ctx) = MakeDoc();
        ctx.Opacity = 0f;
        var b = new BrushTool();
        b.OnPointerDown(new SKPoint(10, 10), ctx);
        b.OnPointerMove(new SKPoint(30, 30), ctx);
        b.OnPointerUp(new SKPoint(30, 30), ctx);

        Assert.True(layer.IsAllWhite());
        Assert.Equal(0, doc.History.Cursor);
        Assert.False(doc.History.IsDirtySinceSave);
    }

    [Fact]
    public void A_shape_at_zero_opacity_records_nothing()
    {
        var (doc, layer, ctx) = MakeDoc();
        ctx.Opacity = 0f;
        var t = new RectShapeTool();
        t.OnPointerDown(new SKPoint(5, 5), ctx);
        t.OnPointerMove(new SKPoint(30, 30), ctx);
        t.OnPointerUp(new SKPoint(30, 30), ctx);

        Assert.True(layer.IsAllWhite());
        Assert.Equal(0, doc.History.Cursor);
    }

    [Fact]
    public void A_stroke_at_full_opacity_is_still_recorded()
    {
        var (doc, layer, ctx) = MakeDoc();
        ctx.ToolSize = 6;
        var b = new BrushTool();
        b.OnPointerDown(new SKPoint(10, 10), ctx);
        b.OnPointerUp(new SKPoint(10, 10), ctx);

        Assert.Equal(SKColors.Black, layer.Bitmap.GetPixel(10, 10));
        Assert.Equal(1, doc.History.Cursor);
    }

    [Fact]
    public void Text_of_nothing_but_spaces_records_nothing()
    {
        var (doc, layer, ctx) = MakeDoc();
        var t = new TextTool();
        t.OnPointerDown(new SKPoint(10, 20), ctx);
        t.CommitText("   ");

        Assert.True(layer.IsAllWhite());
        Assert.Equal(0, doc.History.Cursor);
    }

    [Fact]
    public void Text_at_zero_opacity_records_nothing()
    {
        var (doc, _, ctx) = MakeDoc();
        ctx.Opacity = 0f;
        var t = new TextTool();
        t.OnPointerDown(new SKPoint(10, 20), ctx);
        t.CommitText("Hi");

        Assert.Equal(0, doc.History.Cursor);
    }

    // ───────── текст в несколько строк ─────────

    [Fact]
    public void Every_line_of_a_multiline_text_is_drawn()
    {
        var (doc, layer, ctx) = MakeDoc(200);
        var t = new TextTool();
        t.OnPointerDown(new SKPoint(20, 40), ctx);
        t.CommitText("AAA\nBBB");

        Assert.Equal(1, doc.History.Cursor);
        // DrawText рисует один ряд глифов: «\n» уходил в шрифт наравне с буквами и выходил
        // прямоугольником-заглушкой, а вторая строка ложилась той же строкой дальше вправо.
        Assert.True(HasInk(layer, 20, 45), "первая строка не нарисована");
        Assert.True(HasInk(layer, 46, 90), "вторая строка не нарисована");
    }

    [Fact]
    public void Windows_line_breaks_count_as_line_breaks_too()
    {
        var (doc, layer, ctx) = MakeDoc(200);
        var t = new TextTool();
        t.OnPointerDown(new SKPoint(20, 40), ctx);
        t.CommitText("AAA\r\nBBB");

        Assert.True(HasInk(layer, 46, 90), "вторая строка не нарисована");
    }

    private static bool HasInk(PixelLayer layer, int fromY, int toY)
    {
        for (int y = fromY; y < toY && y < layer.Height; y++)
            for (int x = 0; x < layer.Width; x++)
                if (layer.Bitmap.GetPixel(x, y) != SKColors.White) return true;
        return false;
    }

    // ───────── имена слоёв ─────────

    [Fact]
    public void A_new_layer_never_repeats_a_name_already_in_the_stack()
    {
        var vm = new MainViewModel();
        vm.AddLayerCommand.Execute(null);
        vm.AddLayerCommand.Execute(null);
        vm.RemoveLayerCommand.Execute(vm.LayerItems[1]);
        vm.AddLayerCommand.Execute(null);

        var names = vm.Document.Layers.Select(l => l.Name).ToList();
        // Номер брался из числа слоёв, а оно уменьшается при удалении: в панели появлялись
        // две одинаковые строки, и выключить видимость можно было не тому слою.
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    // ───────── разбор размера холста ─────────

    [Theory]
    [InlineData("1200x800")]
    [InlineData("1200X800")]
    [InlineData("1200х800")]   // русская «х»: раскладка в этот момент русская
    [InlineData("1200Х800")]
    [InlineData("1200×800")]
    [InlineData("1200*800")]
    [InlineData(" 1200 x 800 ")]
    public void Canvas_size_is_understood_in_every_way_it_gets_typed(string input)
    {
        Assert.True(MainViewModel.TryParseCanvasSize(input, out var w, out var h));
        Assert.Equal(1200, w);
        Assert.Equal(800, h);
    }

    [Theory]
    [InlineData("1200")]
    [InlineData("1200x")]
    [InlineData("много на много")]
    [InlineData("")]
    public void Nonsense_size_is_rejected_rather_than_silently_ignored(string input)
    {
        Assert.False(MainViewModel.TryParseCanvasSize(input, out _, out _));
    }

    // ───────── координаты пикселя округляются вниз, а не к нулю ─────────

    [Fact]
    public void A_click_just_outside_the_left_edge_fills_nothing()
    {
        var (doc, layer, ctx) = MakeDoc();
        // Приведение к int отбрасывает дробную часть в сторону нуля, и всё от -0.99 до 0
        // схлопывалось в ноль: точка мимо холста становилась его левым верхним пикселем.
        new FillTool().OnPointerDown(new SKPoint(-0.4f, -0.4f), ctx);

        Assert.True(layer.IsAllWhite());
        Assert.Equal(0, doc.History.Cursor);
    }

    [Fact]
    public void A_click_just_outside_the_left_edge_picks_no_colour()
    {
        var (doc, layer, ctx) = MakeDoc();
        FillRect(layer, new SKRect(0, 0, 2, 2), SKColors.Red);
        ctx.PrimaryColor = SKColors.Blue;

        new PickerTool().OnPointerDown(new SKPoint(-0.4f, -0.4f), ctx);

        Assert.Equal(SKColors.Blue, ctx.PrimaryColor);
    }

    [Fact]
    public void A_click_on_the_first_pixel_still_picks_its_colour()
    {
        var (doc, layer, ctx) = MakeDoc();
        FillRect(layer, new SKRect(0, 0, 2, 2), SKColors.Red);

        new PickerTool().OnPointerDown(new SKPoint(0.4f, 0.4f), ctx);

        Assert.Equal(SKColors.Red, ctx.PrimaryColor);
    }
}
