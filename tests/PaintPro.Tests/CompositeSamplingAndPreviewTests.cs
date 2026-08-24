using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Одна сборка документа на всех: экран, файл, буфер обмена и пипетка обязаны показывать
/// одно и то же. Здесь проверяется то, что до сих пор собиралось мимо неё - выборка цвета
/// точки, превью ластика и судьба поднятого объекта при операциях над всем документом.
/// </summary>
public class CompositeSamplingAndPreviewTests
{
    private static (Document doc, ToolContext ctx) Make(int size = 60)
    {
        var doc = new Document(size, size);
        return (doc, new ToolContext(doc) { PrimaryColor = SKColors.Black, Opacity = 1f, ToolSize = 6f });
    }

    private static void Paint(Layer layer, SKRect r, SKColor color)
    {
        using var canvas = new SKCanvas(((PixelLayer)layer).Bitmap);
        using var paint = new SKPaint { Color = color };
        canvas.DrawRect(r, paint);
    }

    private static FloatingPickup Float(Document doc, SKRect where, SKColor color)
    {
        var src = new SKBitmap((int)where.Width, (int)where.Height,
                               SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(src)) c.Clear(color);
        var fp = new FloatingPickup(src, where)
        {
            OriginalAreaErased = true,
            SourceLayerId = doc.ActiveLayer.Id,
        };
        doc.FloatingPickup = fp;
        return fp;
    }

    private static SKBitmap RenderToBitmap(Document doc, ITool? tool)
    {
        var bmp = new SKBitmap(doc.CanvasWidth, doc.CanvasHeight,
                               SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        if (tool is null) doc.Render(canvas);
        else doc.Render(canvas, tool.PreviewBitmap, tool.PreviewAlpha,
                        previewBlend: tool.PreviewBlendMode);
        return bmp;
    }

    // ───────── Пипетка и статусбар видят поднятый объект ─────────

    [Fact]
    public void Sampling_reads_the_floating_object_not_what_is_under_it()
    {
        var (doc, _) = Make();
        Paint(doc.Layers[0], new SKRect(0, 0, 60, 60), SKColors.Red);
        Float(doc, new SKRect(10, 10, 30, 30), SKColors.Blue);

        Assert.Equal(SKColors.Blue, doc.SampleComposite(20, 20));
        // Вне объекта - по-прежнему то, что лежит на слое.
        Assert.Equal(SKColors.Red, doc.SampleComposite(50, 50));
    }

    [Fact]
    public void Sampling_respects_the_rotation_of_the_floating_object()
    {
        var (doc, _) = Make();
        Paint(doc.Layers[0], new SKRect(0, 0, 60, 60), SKColors.Red);
        // Узкая горизонтальная полоса, повёрнутая на 90 градусов: становится вертикальной.
        var fp = Float(doc, new SKRect(20, 28, 40, 32), SKColors.Blue);
        fp.SetRotation(MathF.PI / 2f);

        // Точка внутри повёрнутой полосы, но за неповёрнутым габаритом.
        Assert.Equal(SKColors.Blue, doc.SampleComposite(30, 22));
        // Точка внутри неповёрнутого габарита, но объекта там уже нет.
        Assert.Equal(SKColors.Red, doc.SampleComposite(22, 30));
    }

    [Fact]
    public void Sampling_respects_the_quad_mask_of_the_floating_object()
    {
        var (doc, _) = Make();
        Paint(doc.Layers[0], new SKRect(0, 0, 60, 60), SKColors.Red);
        var fp = Float(doc, new SKRect(10, 10, 40, 40), SKColors.Blue);
        // Треугольная маска: правый нижний угол габарита остаётся вне её.
        fp.Quad = new[]
        {
            new SKPoint(10, 10), new SKPoint(40, 10),
            new SKPoint(10, 40), new SKPoint(10, 40),
        };

        Assert.Equal(SKColors.Blue, doc.SampleComposite(14, 14));
        Assert.Equal(SKColors.Red, doc.SampleComposite(38, 38));
    }

    [Fact]
    public void Sampling_agrees_with_the_screen_down_to_layer_opacity()
    {
        var (doc, _) = Make();
        Paint(doc.Layers[0], new SKRect(0, 0, 60, 60), SKColors.Black);
        doc.Layers[0].Opacity = 0.5f;
        Float(doc, new SKRect(10, 10, 30, 30), SKColors.Black);

        // Смысл выборки в том, чтобы совпадать с картинкой, а не в конкретном числе:
        // сравниваем с той самой сборкой, которая идёт на экран и в файл. Допуск в единицу -
        // Skia смешивает в целых предумноженных, выборка в float, и на полупрозрачном слое
        // они расходятся на последний бит. Ошибка «взяли цвет не того слоя» - это десятки.
        using var screen = RenderToBitmap(doc, null);
        foreach (var (x, y) in new[] { (20, 20), (12, 12), (50, 50), (10, 40) })
        {
            var shown = screen.GetPixel(x, y);
            var sampled = doc.SampleComposite(x, y);
            Assert.InRange(sampled.Red, shown.Red - 1, shown.Red + 1);
            Assert.InRange(sampled.Green, shown.Green - 1, shown.Green + 1);
            Assert.InRange(sampled.Blue, shown.Blue - 1, shown.Blue + 1);
        }
    }

    [Fact]
    public void Sampling_outside_the_canvas_is_still_transparent()
    {
        var (doc, _) = Make();
        Float(doc, new SKRect(10, 10, 30, 30), SKColors.Blue);
        Assert.Equal(SKColors.Transparent, doc.SampleComposite(-1, 20));
        Assert.Equal(SKColors.Transparent, doc.SampleComposite(20, 60));
    }

    // ───────── Превью ластика показывает результат, а не белую краску ─────────

    [Fact]
    public void Eraser_preview_on_an_upper_layer_shows_the_hole_it_will_leave()
    {
        var (doc, ctx) = Make();
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "L2"), doc);
        doc.ActiveLayerIndex = 1;
        Paint(doc.Layers[0], new SKRect(0, 0, 60, 60), SKColors.Red);
        Paint(doc.Layers[1], new SKRect(0, 0, 60, 60), SKColors.Blue);

        var eraser = new EraserTool();
        eraser.OnPointerDown(new SKPoint(30, 30), ctx);

        // Экран во время штриха: под ластиком обязан проступить нижний слой.
        using var mid = RenderToBitmap(doc, eraser);
        Assert.Equal(SKColors.Red, mid.GetPixel(30, 30));

        eraser.OnPointerUp(new SKPoint(30, 30), ctx);
        // И после отпускания там же ровно то же самое.
        using var after = RenderToBitmap(doc, null);
        Assert.Equal(SKColors.Red, after.GetPixel(30, 30));
    }

    [Fact]
    public void Eraser_preview_does_not_punch_through_the_layers_below()
    {
        var (doc, ctx) = Make();
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "L2"), doc);
        doc.ActiveLayerIndex = 1;
        Paint(doc.Layers[0], new SKRect(0, 0, 60, 60), SKColors.Red);
        Paint(doc.Layers[1], new SKRect(0, 0, 60, 60), SKColors.Blue);

        var eraser = new EraserTool();
        eraser.OnPointerDown(new SKPoint(30, 30), ctx);
        using var mid = RenderToBitmap(doc, eraser);

        // Ластик снимает пиксели своего слоя, а не всё до самой бумаги.
        Assert.NotEqual(SKColors.White, mid.GetPixel(30, 30));
    }

    [Fact]
    public void Eraser_preview_on_the_bottom_layer_still_paints_paper_white()
    {
        var (doc, ctx) = Make();
        Paint(doc.Layers[0], new SKRect(0, 0, 60, 60), SKColors.Red);

        var eraser = new EraserTool();
        eraser.OnPointerDown(new SKPoint(30, 30), ctx);
        using var mid = RenderToBitmap(doc, eraser);
        Assert.Equal(SKColors.White, mid.GetPixel(30, 30));
    }

    [Fact]
    public void Brush_preview_is_unchanged_by_the_eraser_fix()
    {
        var (doc, ctx) = Make();
        ctx.PrimaryColor = SKColors.Blue;
        var brush = new BrushTool();
        brush.OnPointerDown(new SKPoint(30, 30), ctx);
        using var mid = RenderToBitmap(doc, brush);
        Assert.Equal(SKColors.Blue, mid.GetPixel(30, 30));
    }

    // ───────── Операции над всем документом снимают поднятый объект ─────────

    [Fact]
    public void Crop_commits_the_floating_object_instead_of_dropping_it()
    {
        var (doc, ctx) = Make();
        Paint(doc.Layers[0], new SKRect(10, 10, 30, 30), SKColors.Blue);
        doc.Selection = new RectSelection(new SKRect(10, 10, 30, 30));
        var sel = new SelectTool();
        sel.OnPointerDown(new SKPoint(20, 20), ctx);
        sel.OnPointerMove(new SKPoint(15, 15), ctx);   // сдвинули на (-5,-5)
        sel.OnPointerUp(new SKPoint(15, 15), ctx);
        Assert.NotNull(doc.FloatingPickup);

        var crop = new CropTool();
        crop.OnPointerDown(new SKPoint(0, 0), ctx);
        crop.OnPointerMove(new SKPoint(40, 40), ctx);
        crop.OnPointerUp(new SKPoint(40, 40), ctx);

        Assert.Null(doc.FloatingPickup);
        Assert.Equal(40, doc.CanvasWidth);
        // Объект прижался ДО кадрирования, значит он на холсте, а не потерян.
        Assert.Equal(SKColors.Blue, ((PixelLayer)doc.Layers[0]).Bitmap.GetPixel(15, 15));
    }

    [Fact]
    public void Replacing_every_layer_never_leaves_a_stale_floating_object()
    {
        var (doc, _) = Make();
        Paint(doc.Layers[0], new SKRect(0, 0, 60, 60), SKColors.Red);
        Float(doc, new SKRect(10, 10, 30, 30), SKColors.Blue);

        doc.History.ExecuteAndPush(DocumentTransform.Rotate(doc, MathF.PI / 2f), doc);
        Assert.Null(doc.FloatingPickup);

        // И на откате тоже: объекту не к чему возвращаться, слои заменены целиком.
        Float(doc, new SKRect(10, 10, 30, 30), SKColors.Blue);
        doc.History.Undo(doc);
        Assert.Null(doc.FloatingPickup);
    }

    [Fact]
    public void Resizing_the_canvas_never_leaves_a_stale_floating_object()
    {
        var (doc, _) = Make();
        Float(doc, new SKRect(10, 10, 30, 30), SKColors.Blue);
        doc.History.ExecuteAndPush(new ResizeCanvasCommand(30, 30), doc);
        Assert.Null(doc.FloatingPickup);
    }

    // ───────── Ручки и хват меряются в экранных пикселях ─────────

    [Fact]
    public void Quad_corner_grab_radius_is_measured_on_screen_not_on_the_canvas()
    {
        var (doc, ctx) = Make(400);
        ctx.Zoom = 4.0;    // одна точка документа = четыре экранных
        doc.Selection = new PolygonSelection(
            new SKPoint(100, 100), new SKPoint(200, 100),
            new SKPoint(200, 200), new SKPoint(100, 200));
        var quad = new QuadTool();

        // 6 точек документа от угла - это 24 экранных, то есть заметно дальше ручки.
        // Клик обязан попасть внутрь выделения и поднять его, а не схватить угол.
        quad.OnPointerDown(new SKPoint(106, 106), ctx);

        Assert.NotNull(doc.FloatingPickup);
        var quadCorners = doc.FloatingPickup!.Quad!;
        quad.OnPointerMove(new SKPoint(116, 116), ctx);
        quad.OnPointerUp(new SKPoint(116, 116), ctx);

        // Двигался весь объект: все четыре угла уехали на один и тот же вектор,
        // а не один угол за курсором.
        Assert.Equal(110f, quadCorners[0].X, 3);
        Assert.Equal(210f, quadCorners[1].X, 3);
    }

    [Fact]
    public void Quad_corner_stays_grabbable_when_zoomed_out()
    {
        var (doc, ctx) = Make(400);
        ctx.Zoom = 0.25;   // одна точка документа = четверть экранной
        doc.Selection = new PolygonSelection(
            new SKPoint(100, 100), new SKPoint(200, 100),
            new SKPoint(200, 200), new SKPoint(100, 200));
        var quad = new QuadTool();

        // 20 точек документа - это 5 экранных, то есть всё ещё «по ручке».
        quad.OnPointerDown(new SKPoint(120, 120), ctx);
        quad.OnPointerMove(new SKPoint(130, 130), ctx);
        quad.OnPointerUp(new SKPoint(130, 130), ctx);

        var ps = Assert.IsType<PolygonSelection>(doc.Selection);
        Assert.Equal(130f, ps.Corners[0].X, 3);
    }

    // ───────── Восстановленный слой равен холсту ─────────

    [Fact]
    public void A_layer_is_always_added_at_the_current_canvas_size()
    {
        var doc = new Document(40, 40);
        var add = LayerStackCommand.Add(doc, "L2");
        doc.History.ExecuteAndPush(new ResizeCanvasCommand(120, 120), doc);
        doc.History.ExecuteAndPush(add, doc);

        var top = (PixelLayer)doc.Layers[1];
        Assert.Equal(120, top.Width);
        Assert.Equal(120, top.Height);
    }
}
