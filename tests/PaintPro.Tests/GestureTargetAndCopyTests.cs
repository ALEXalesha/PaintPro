using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Жест начинается на одном слое, а исполняется командой на отпускании - и до 1.17.0
/// команда спрашивала «какой слой активен СЕЙЧАС». Между двумя этими мгновениями слой
/// успевает смениться: у окна ввода текста между кликом и OK проходит сколько угодно
/// времени, а горячие клавиши работают и с зажатой кнопкой мыши. Штрих тогда ложился не
/// туда, куда его вело превью, а ластик вместо дыры красил белым - режим слияния тоже
/// пересчитывался на отпускании.
/// </summary>
public class GestureTargetAndCopyTests
{
    private static Document TwoLayers(out PixelLayer paper, out PixelLayer top)
    {
        var doc = new Document(40, 40);
        doc.Layers.Add(new PixelLayer(40, 40, SKColors.Transparent) { Name = "Верхний" });
        doc.ActiveLayerIndex = 1;
        paper = (PixelLayer)doc.Layers[0];
        top = (PixelLayer)doc.Layers[1];
        return doc;
    }

    [Fact]
    public void A_stroke_lands_on_the_layer_it_started_on()
    {
        var doc = TwoLayers(out var paper, out var top);
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Red, Opacity = 1f, ToolSize = 6f };

        var brush = new BrushTool();
        brush.OnPointerDown(new SKPoint(20, 20), ctx);
        doc.ActiveLayerIndex = 0;                 // слой сменили посреди жеста
        brush.OnPointerUp(new SKPoint(20, 20), ctx);

        Assert.Equal(SKColors.Red, top.Bitmap.GetPixel(20, 20));
        Assert.Equal(SKColors.White, paper.Bitmap.GetPixel(20, 20));
    }

    [Fact]
    public void A_shape_lands_on_the_layer_it_started_on()
    {
        var doc = TwoLayers(out var paper, out var top);
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Red, Opacity = 1f, ToolSize = 3f };

        var tool = new RectShapeTool { Fill = true };
        tool.OnPointerDown(new SKPoint(10, 10), ctx);
        tool.OnPointerMove(new SKPoint(30, 30), ctx);
        doc.ActiveLayerIndex = 0;
        tool.OnPointerUp(new SKPoint(30, 30), ctx);

        Assert.Equal(SKColors.Red, top.Bitmap.GetPixel(20, 20));
        Assert.Equal(SKColors.White, paper.Bitmap.GetPixel(20, 20));
    }

    /// <summary>
    /// Ластику это стоит не только слоя: на нижнем он красит белым, на верхних вычитает
    /// пиксели. Пересчёт на отпускании отвечал про другой слой, и вместо дыры на верхнем
    /// слое оставалась белая полоса - при том, что превью всю дорогу показывало дыру.
    /// </summary>
    [Fact]
    public void The_eraser_keeps_the_blend_mode_it_started_with()
    {
        var doc = TwoLayers(out _, out var top);
        using (var c = new SKCanvas(top.Bitmap)) c.Clear(SKColors.Blue);

        var ctx = new ToolContext(doc) { ToolSize = 8f, Opacity = 1f };
        var eraser = new EraserTool();
        eraser.OnPointerDown(new SKPoint(20, 20), ctx);
        doc.ActiveLayerIndex = 0;
        eraser.OnPointerUp(new SKPoint(20, 20), ctx);

        Assert.Equal(0, top.Bitmap.GetPixel(20, 20).Alpha);
    }

    /// <summary>
    /// Текст спрашивают в модальном окне, и слой между кликом и ответом меняется совсем
    /// уж свободно.
    /// </summary>
    [Fact]
    public void Text_lands_on_the_layer_the_click_was_made_on()
    {
        var doc = TwoLayers(out var paper, out var top);
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Black, Opacity = 1f };

        var tool = new TextTool();
        tool.OnPointerDown(new SKPoint(5, 25), ctx);
        doc.ActiveLayerIndex = 0;
        tool.CommitText("A");

        bool inkOnTop = false;
        for (int y = 0; y < 40 && !inkOnTop; y++)
            for (int x = 0; x < 40; x++)
                if (top.Bitmap.GetPixel(x, y).Alpha > 0) { inkOnTop = true; break; }

        Assert.True(inkOnTop);
        Assert.True(paper.IsAllWhite());
    }

    /// <summary>
    /// Слой с прозрачностью в ноль - тот же скрытый слой, только другой дорогой: ползунок
    /// в панели начинается с нуля, довести его туда - одно движение. Рисовать в такой
    /// слой так же бессмысленно, и молчать об этом так же нельзя.
    /// </summary>
    [Fact]
    public void A_fully_transparent_layer_refuses_the_brush_and_says_why()
    {
        var doc = TwoLayers(out _, out _);
        doc.Layers[1].Opacity = 0f;

        string? hint = null;
        var ctx = new ToolContext(doc) { ReportHint = h => hint = h };

        Assert.Null(ctx.DrawTarget());
        Assert.NotNull(hint);
        Assert.Contains("прозрач", hint);
    }

    [Fact]
    public void A_transparent_layer_records_no_stroke()
    {
        var doc = TwoLayers(out _, out _);
        doc.Layers[1].Opacity = 0f;
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Red, Opacity = 1f, ToolSize = 6f };

        var brush = new BrushTool();
        brush.OnPointerDown(new SKPoint(20, 20), ctx);
        brush.OnPointerUp(new SKPoint(20, 20), ctx);

        Assert.Equal(0, doc.History.Cursor);
    }

    // ───────── копирование того, чего нет ─────────

    /// <summary>
    /// Объект, уведённый целиком за край холста. Прежде копирование отдавало прозрачный
    /// пиксель 1x1: он честно уходил в буфер обмена вместо того, что там лежало, и
    /// Ctrl+C заканчивался «успешно» и молча.
    /// </summary>
    [Fact]
    public void Copying_a_pickup_dragged_off_the_canvas_reports_that_there_is_nothing()
    {
        var doc = new Document(60, 60);
        PickupOps.PromoteRect(doc, new SKRect(10, 10, 40, 40));
        PickupOps.Translate(doc.FloatingPickup!, -900, -900);

        Assert.Null(ClipboardService.ExtractForClipboard(doc));
        Assert.Equal(CopyStatus.Nothing, new ClipboardService().Copy(doc));
    }

    /// <summary>
    /// А Ctrl+X по такому объекту не должен его выбрасывать: вырезание идёт только вслед
    /// за удавшимся копированием, иначе пиксели пропадают в никуда.
    /// </summary>
    [Fact]
    public void Cut_of_an_off_canvas_pickup_keeps_the_object()
    {
        var vm = new MainViewModel();
        PickupOps.PromoteRect(vm.Document, new SKRect(10, 10, 40, 40));
        PickupOps.Translate(vm.Document.FloatingPickup!, -2000, -2000);

        vm.CutSelectionCommand.Execute(null);

        Assert.NotNull(vm.Document.FloatingPickup);
        Assert.Contains("нечего", vm.StatusHint);
    }
}
