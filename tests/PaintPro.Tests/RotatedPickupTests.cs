using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Поворот плавающего объекта: попадание мыши в тело, углы quad'а и габарит для
/// копирования. Всё это хранится неповёрнутым, а рисуется с поворотом, и пока поворот
/// учитывала одна отрисовка, ввод сравнивал мышь с фигурой, которой на экране нет.
/// </summary>
public class RotatedPickupTests
{
    private static (Document doc, PixelLayer layer, ToolContext ctx) MakeDoc(int size = 100)
    {
        var doc = new Document(size, size);
        var layer = (PixelLayer)doc.ActiveLayer;
        using (var c = new SKCanvas(layer.Bitmap))
        using (var p = new SKPaint { Color = SKColors.Red })
            c.DrawRect(new SKRect(5, 5, size - 5, size - 5), p);
        return (doc, layer, new ToolContext(doc) { PrimaryColor = SKColors.Black, Opacity = 1f });
    }

    /// <summary>Поднять прямоугольник (30,45)-(70,55) и повернуть на 90°.</summary>
    private static (Document doc, ToolContext ctx, SelectTool tool) LiftAndRotate()
    {
        var (doc, _, ctx) = MakeDoc();
        doc.Selection = new RectSelection(new SKRect(30, 45, 70, 55));
        var tool = new SelectTool();
        tool.OnPointerDown(new SKPoint(50, 50), ctx);
        tool.OnPointerUp(new SKPoint(50, 50), ctx);
        doc.FloatingPickup!.SetRotation(MathF.PI / 2f);   // стал вертикальным: x∈[45,55], y∈[30,70]
        return (doc, ctx, tool);
    }

    [Fact]
    public void Click_on_a_rotated_object_grabs_it_instead_of_dropping_it()
    {
        var (doc, ctx, tool) = LiftAndRotate();
        int history = doc.History.UndoDepth;

        // Точка на самом объекте, но за неповёрнутым габаритом (y=35 при габарите 45..55).
        tool.OnPointerDown(new SKPoint(50, 35), ctx);

        // Раньше клик по видимому краю прижимал объект к холсту и начинал новое выделение.
        Assert.NotNull(doc.FloatingPickup);
        Assert.Equal(history, doc.History.UndoDepth);

        tool.OnPointerMove(new SKPoint(50, 25), ctx);
        Assert.Equal(35f, doc.FloatingPickup!.Y, 3);   // сдвинулся на 10 вверх
    }

    [Fact]
    public void Click_on_an_empty_corner_of_a_rotated_object_lets_it_go()
    {
        var (doc, ctx, tool) = LiftAndRotate();

        // Внутри неповёрнутого габарита, но объекта там уже нет.
        tool.OnPointerDown(new SKPoint(33, 46), ctx);

        Assert.Null(doc.FloatingPickup);
    }

    [Fact]
    public void Quad_pickup_is_grabbed_by_its_shape_not_by_its_bbox()
    {
        var (doc, _, ctx) = MakeDoc();
        // Трапеция: левый нижний угол срезан, так что (15,55) внутри габарита, но вне фигуры.
        doc.Selection = new PolygonSelection(
            new SKPoint(10, 10), new SKPoint(60, 10), new SKPoint(60, 60), new SKPoint(40, 60));
        var tool = new QuadTool();
        tool.OnPointerDown(new SKPoint(50, 30), ctx);
        tool.OnPointerUp(new SKPoint(50, 30), ctx);
        Assert.NotNull(doc.FloatingPickup);

        tool.OnPointerDown(new SKPoint(15, 55), ctx);

        Assert.Null(doc.FloatingPickup);
    }

    [Fact]
    public void Quad_pickup_still_moves_when_grabbed_inside_its_shape()
    {
        var (doc, _, ctx) = MakeDoc();
        doc.Selection = new PolygonSelection(
            new SKPoint(10, 10), new SKPoint(60, 10), new SKPoint(60, 60), new SKPoint(40, 60));
        var tool = new QuadTool();
        tool.OnPointerDown(new SKPoint(50, 30), ctx);
        tool.OnPointerUp(new SKPoint(50, 30), ctx);

        tool.OnPointerDown(new SKPoint(50, 55), ctx);

        Assert.NotNull(doc.FloatingPickup);
    }

    [Fact]
    public void Corner_of_a_rotated_quad_is_grabbed_where_it_is_drawn()
    {
        var (doc, _, ctx) = MakeDoc();
        doc.Selection = new PolygonSelection(
            new SKPoint(20, 20), new SKPoint(60, 20), new SKPoint(60, 40), new SKPoint(20, 40));
        var tool = new QuadTool();
        tool.OnPointerDown(new SKPoint(40, 30), ctx);
        tool.OnPointerUp(new SKPoint(40, 30), ctx);

        var fp = doc.FloatingPickup!;
        fp.SetRotation(MathF.PI / 2f);
        // Точка (50,10) - это то место, где после поворота нарисован угол (20,20).
        var drawn = GeometryMath.Rotate(fp.Quad![0], fp.Center, fp.Rotation);
        var target = GeometryMath.Rotate(new SKPoint(30, 20), fp.Center, fp.Rotation);

        tool.OnPointerDown(drawn, ctx);
        tool.OnPointerMove(target, ctx);
        tool.OnPointerUp(target, ctx);

        // Хват попал именно в угол, а не мимо всего объекта (мимо - это прижать его к холсту).
        Assert.Same(fp, doc.FloatingPickup);
        // И угол уехал туда, куда его привели: раньше в него писалась мировая координата,
        // и фигура прыгала в сторону на весь поворот.
        Assert.Equal(30f, fp.Quad[0].X, 2);
        Assert.Equal(20f, fp.Quad[0].Y, 2);
    }

    [Fact]
    public void Copying_a_rotated_quad_takes_the_area_it_is_drawn_in()
    {
        var bmp = new SKBitmap(40, 40, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pickup = new FloatingPickup(bmp, new SKRect(0, 0, 40, 40))
        {
            Quad = new[]
            {
                new SKPoint(10, 10), new SKPoint(30, 10),
                new SKPoint(30, 20), new SKPoint(10, 20),
            },
            Rotation = MathF.PI / 2f,
        };

        var b = Document.PickupBounds(pickup, 100, 100);

        // Центр (20,20): полоса 20×10 после поворота стоит вертикально.
        Assert.Equal(20, b.Left);
        Assert.Equal(10, b.Top);
        Assert.Equal(30, b.Right);
        Assert.Equal(30, b.Bottom);
    }

    [Fact]
    public void Switching_tools_in_the_middle_of_a_gesture_releases_the_handles()
    {
        var (doc, _, ctx) = MakeDoc();
        var tool = new SelectTool();
        tool.OnPointerDown(new SKPoint(10, 10), ctx);   // кнопка мыши всё ещё нажата
        Assert.True(ctx.IsDrawing);

        tool.OnDeactivate(ctx);                         // инструмент сменили горячей клавишей

        // Брошенный включённым признак «идёт жест» прячет ручки плавающего объекта
        // насовсем: MouseUp придёт уже другому инструменту, и выключить его некому.
        Assert.False(ctx.IsDrawing);
    }
}
