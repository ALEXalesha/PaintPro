using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Кеинг фона при подъёме выделения, габарит фигуры при фиксации и подъём со
/// скрытого слоя.
/// </summary>
public class KeyingAndShapeBoundsTests
{
    // ───────── кеинг фона ─────────

    [Fact]
    public void KeyOutBackground_keeps_white_enclosed_by_the_drawing()
    {
        // Чёрное кольцо на белом: центр белый, но он внутри рисунка.
        using var src = new SKBitmap(5, 5, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var c = new SKCanvas(src)) c.Clear(SKColors.White);
        foreach (var (x, y) in new[] { (1, 1), (2, 1), (3, 1), (1, 2), (3, 2), (1, 3), (2, 3), (3, 3) })
            src.SetPixel(x, y, SKColors.Black);

        using var keyed = BitmapKeying.KeyOutBackground(src, SKColors.White);

        Assert.Equal((byte)0, keyed.GetPixel(0, 0).Alpha);     // фон снаружи ушёл
        Assert.Equal((byte)255, keyed.GetPixel(2, 2).Alpha);   // белое внутри рисунка осталось
        Assert.Equal(SKColors.Black, keyed.GetPixel(2, 1));    // сам рисунок цел
    }

    [Fact]
    public void KeyOutBackground_stops_at_the_edge_of_the_drawing()
    {
        // Сплошная чёрная рамка по краю: фону внутрь не пройти.
        using var src = new SKBitmap(3, 3, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var c = new SKCanvas(src)) c.Clear(SKColors.Black);
        src.SetPixel(1, 1, SKColors.White);

        using var keyed = BitmapKeying.KeyOutBackground(src, SKColors.White);

        Assert.Equal((byte)255, keyed.GetPixel(1, 1).Alpha);
    }

    [Fact]
    public void Lifting_a_selection_does_not_punch_holes_through_white_content()
    {
        var doc = new Document(30, 30);
        var layer = (PixelLayer)doc.ActiveLayer;
        using (var c = new SKCanvas(layer.Bitmap))
        {
            using var black = new SKPaint { Color = SKColors.Black };
            c.DrawRect(new SKRect(8, 8, 22, 22), black);          // чёрный квадрат
            using var white = new SKPaint { Color = SKColors.White };
            c.DrawRect(new SKRect(12, 12, 18, 18), white);        // и белая заливка внутри него
        }

        PickupOps.PromoteRect(doc, new SKRect(5, 5, 25, 25));

        // Белое внутри рисунка обязано уехать вместе с ним: иначе сквозь дыру видно то,
        // что лежит ниже.
        var pickup = doc.FloatingPickup!;
        int localX = 15 - 5, localY = 15 - 5;
        Assert.Equal((byte)255, pickup.SourceBitmap.GetPixel(localX, localY).Alpha);
        // А фон вокруг квадрата - прозрачный.
        Assert.Equal((byte)0, pickup.SourceBitmap.GetPixel(1, 1).Alpha);
    }

    // ───────── габарит фигуры ─────────

    [Fact]
    public void A_horizontal_arrow_keeps_its_head_when_committed()
    {
        var doc = new Document(60, 60);
        var layer = (PixelLayer)doc.ActiveLayer;
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Black, ToolSize = 4f, Opacity = 1f };

        var tool = new ArrowShapeTool();
        tool.OnPointerDown(new SKPoint(10, 30), ctx);
        tool.OnPointerMove(new SKPoint(50, 30), ctx);
        tool.OnPointerUp(new SKPoint(50, 30), ctx);

        // Наконечник стоит поперёк линии, а прямоугольник жеста у горизонтальной стрелки
        // вырожден в отрезок: крылья оказывались за границей вырезаемой области, и на
        // слой ложилась стрелка с обрубленным наконечником.
        Assert.NotEqual(SKColors.White, layer.Bitmap.GetPixel(30, 30));  // древко
        Assert.NotEqual(SKColors.White, layer.Bitmap.GetPixel(35, 38));  // нижнее крыло
        Assert.NotEqual(SKColors.White, layer.Bitmap.GetPixel(35, 22));  // верхнее крыло
    }

    [Fact]
    public void A_rectangle_still_lands_inside_its_own_gesture_box()
    {
        var doc = new Document(60, 60);
        var layer = (PixelLayer)doc.ActiveLayer;
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Black, ToolSize = 4f, Opacity = 1f };

        var tool = new RectShapeTool();
        tool.OnPointerDown(new SKPoint(20, 20), ctx);
        tool.OnPointerMove(new SKPoint(40, 40), ctx);
        tool.OnPointerUp(new SKPoint(40, 40), ctx);

        Assert.NotEqual(SKColors.White, layer.Bitmap.GetPixel(20, 30));  // левая сторона
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(5, 5));       // далеко снаружи - чисто
    }

    // ───────── скрытый слой ─────────

    [Fact]
    public void Lifting_a_selection_off_a_hidden_layer_is_refused_with_a_hint()
    {
        var doc = new Document(30, 30);
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Слой 2"), doc);
        doc.ActiveLayer.Visible = false;

        var hint = "";
        var ctx = new ToolContext(doc) { ReportHint = t => hint = t };
        doc.Selection = new RectSelection(5, 5, 20, 20);

        new SelectTool().OnPointerDown(new SKPoint(15, 15), ctx);

        // Пикап рисуется поверх документа всегда: пиксели скрытого слоя всплывали на
        // экране, а после прижатия исчезали обратно.
        Assert.Null(doc.FloatingPickup);
        Assert.NotNull(doc.Selection);
        Assert.Contains("скрыт", hint);
    }

    [Fact]
    public void Lifting_a_quad_off_a_hidden_layer_is_refused_too()
    {
        var doc = new Document(30, 30);
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Слой 2"), doc);
        doc.ActiveLayer.Visible = false;

        var hint = "";
        var ctx = new ToolContext(doc) { ReportHint = t => hint = t };
        doc.Selection = new PolygonSelection(
            new SKPoint(5, 5), new SKPoint(25, 5),
            new SKPoint(25, 25), new SKPoint(5, 25));

        new QuadTool().OnPointerDown(new SKPoint(15, 15), ctx);

        Assert.Null(doc.FloatingPickup);
        Assert.Contains("скрыт", hint);
    }

    [Fact]
    public void A_visible_layer_still_lifts()
    {
        var doc = new Document(30, 30);
        var ctx = new ToolContext(doc);
        doc.Selection = new RectSelection(5, 5, 20, 20);

        new SelectTool().OnPointerDown(new SKPoint(15, 15), ctx);

        Assert.NotNull(doc.FloatingPickup);
    }
}
