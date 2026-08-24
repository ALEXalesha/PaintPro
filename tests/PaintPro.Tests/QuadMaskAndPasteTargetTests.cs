using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Одно перемещение на всех, кеинг фона по слою и вставка мимо скрытого слоя.
///
/// Общее у трёх проверок одно: поднятый объект обязан вести себя одинаково независимо
/// от того, какой инструмент его тащит, с какого слоя его подняли и куда он ляжет.
/// </summary>
public class QuadMaskAndPasteTargetTests
{
    private static void Paint(Layer layer, SKRect r, SKColor color)
    {
        using var canvas = new SKCanvas(((PixelLayer)layer).Bitmap);
        using var paint = new SKPaint { Color = color, BlendMode = SKBlendMode.Src };
        canvas.DrawRect(r, paint);
    }

    private static SKBitmap RenderToBitmap(Document doc)
    {
        var bmp = new SKBitmap(doc.CanvasWidth, doc.CanvasHeight,
                               SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        doc.Render(canvas);
        return bmp;
    }

    // ───────── маска едет вместе с объектом ─────────

    [Fact]
    public void Dragging_a_lifted_polygon_with_the_rectangle_tool_takes_its_mask_along()
    {
        var doc = new Document(60, 60);
        Paint(doc.ActiveLayer, new SKRect(0, 0, 60, 60), SKColors.Blue);
        Paint(doc.ActiveLayer, new SKRect(4, 4, 20, 20), SKColors.Black);

        PickupOps.PromoteQuad(doc, new[]
        {
            new SKPoint(4, 4), new SKPoint(20, 4), new SKPoint(20, 20), new SKPoint(4, 20),
        });

        // «Выделение» умеет тащить и поднятый многоугольник: хоткей поворота поднимает
        // его, не трогая активный инструмент.
        var ctx = new ToolContext(doc);
        var tool = new SelectTool();
        tool.OnPointerDown(new SKPoint(12, 12), ctx);
        tool.OnPointerMove(new SKPoint(42, 42), ctx);
        tool.OnPointerUp(new SKPoint(42, 42), ctx);

        var fp = doc.FloatingPickup!;
        Assert.Equal(new SKPoint(34, 34), fp.Quad![0]);

        using var screen = RenderToBitmap(doc);
        Assert.Equal(SKColors.Black, screen.GetPixel(42, 42));   // объект на новом месте
    }

    // ───────── кеинг фона считается по слою и по форме ─────────

    [Fact]
    public void Lifting_a_polygon_keeps_the_paper_inside_it()
    {
        // Белая бумага с чёрной точкой; форму задаёт сам многоугольник, и белое внутри
        // него - такая же часть выделенного, как точка.
        var doc = new Document(40, 40);
        Paint(doc.ActiveLayer, new SKRect(0, 0, 40, 40), SKColors.Blue);
        Paint(doc.ActiveLayer, new SKRect(2, 2, 18, 18), SKColors.White);
        Paint(doc.ActiveLayer, new SKRect(9, 9, 11, 11), SKColors.Black);

        PickupOps.PromoteQuad(doc, new[]
        {
            new SKPoint(2, 2), new SKPoint(18, 2), new SKPoint(18, 18), new SKPoint(2, 18),
        });

        var fp = doc.FloatingPickup!;
        PickupOps.EnsureLazyErase(doc, fp);
        PickupOps.Translate(fp, 18, 18);

        using var screen = RenderToBitmap(doc);
        Assert.Equal(SKColors.White, screen.GetPixel(22, 22)); // бумага уехала вместе с точкой
        Assert.Equal(SKColors.Black, screen.GetPixel(28, 28)); // и сама точка на месте
    }

    [Fact]
    public void Lifting_from_an_upper_layer_keeps_its_white_pixels()
    {
        // На верхнем слое фон - прозрачность, а не бумага: белое там нарисовано, а не
        // осталось от холста, и выкусывать его нельзя.
        var doc = new Document(40, 40);
        Paint(doc.Layers[0], new SKRect(0, 0, 40, 40), SKColors.Blue);
        doc.Layers.Add(new PixelLayer(40, 40, SKColors.Transparent) { Name = "Верхний" });
        doc.ActiveLayerIndex = 1;
        Paint(doc.Layers[1], new SKRect(2, 2, 18, 18), SKColors.White);

        PickupOps.PromoteRect(doc, new SKRect(2, 2, 18, 18));

        var fp = doc.FloatingPickup!;
        PickupOps.EnsureLazyErase(doc, fp);
        PickupOps.Translate(fp, 18, 18);

        using var screen = RenderToBitmap(doc);
        Assert.Equal(SKColors.White, screen.GetPixel(22, 22)); // белое уехало
        Assert.Equal(SKColors.Blue, screen.GetPixel(8, 8));    // а на прежнем месте - нижний слой
    }

    [Fact]
    public void Lifting_a_rectangle_from_the_paper_still_drops_the_background()
    {
        // Нижний слой - бумага, и белое вокруг рисунка по-прежнему выкусывается: иначе
        // выделение таскает за собой непрозрачный белый прямоугольник.
        var doc = new Document(30, 30);
        Paint(doc.ActiveLayer, new SKRect(10, 10, 20, 20), SKColors.Black);

        PickupOps.PromoteRect(doc, new SKRect(5, 5, 25, 25));

        var fp = doc.FloatingPickup!;
        Assert.Equal((byte)0, fp.SourceBitmap.GetPixel(1, 1).Alpha);
        Assert.Equal((byte)255, fp.SourceBitmap.GetPixel(10, 10).Alpha);
    }

    // ───────── вставка мимо скрытого слоя ─────────

    [Fact]
    public void Paste_onto_a_hidden_layer_is_refused_and_says_why()
    {
        var vm = new MainViewModel();
        vm.Document.Layers.Add(new PixelLayer(vm.Document.CanvasWidth, vm.Document.CanvasHeight,
                                              SKColors.Transparent) { Name = "Скрытый", Visible = false });
        vm.Document.ActiveLayerIndex = 1;

        using var bmp = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Red);

        vm.PasteBitmap(bmp);

        Assert.Null(vm.Document.FloatingPickup);
        Assert.Equal(0, vm.Document.History.Cursor);
        Assert.False(vm.IsDirty);
        Assert.NotEqual("", vm.StatusHint);
    }

    [Fact]
    public void Paste_onto_a_visible_layer_still_lands()
    {
        var vm = new MainViewModel();

        using var bmp = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Red);

        vm.PasteBitmap(bmp);

        Assert.NotNull(vm.Document.FloatingPickup);
        Assert.Equal(1, vm.Document.History.Cursor);
    }
}
