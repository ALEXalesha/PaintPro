using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Две находки про то, что видно на экране: щелчок по ТЕКУЩЕЙ строке ленты и слой,
/// выкрученный ползунком в полную прозрачность.
/// </summary>
public class CurrentRowAndTransparentLayerTests
{
    private static Document Painted(int w = 60, int h = 60)
    {
        var doc = new Document(w, h);
        using var c = new SKCanvas(((PixelLayer)doc.Layers[0]).Bitmap);
        c.DrawRect(new SKRect(10, 10, 50, 50), new SKPaint { Color = SKColors.Red });
        return doc;
    }

    private static void Stroke(Document doc, ToolContext ctx, float x, float y)
    {
        var t = new BrushTool();
        t.OnPointerDown(new SKPoint(x, y), ctx);
        t.OnPointerMove(new SKPoint(x + 4, y + 4), ctx);
        t.OnPointerUp(new SKPoint(x + 4, y + 4), ctx);
    }

    // ───────── щелчок по текущей строке ленты ─────────

    /// <summary>
    /// Строка ленты обязана означать одно и то же состояние документа, каким бы путём на
    /// неё ни пришли. Щелчок по ТЕКУЩЕЙ строке возвращался из <see cref="HistoryManager.JumpTo"/>
    /// сразу и поднятое в руках не трогал: на одной и той же позиции получалось то с
    /// картинкой в руках, то без - смотря щёлкнули по ней самой или пришли на неё с
    /// соседней. Это тот же четвёртый инвариант, что проверяет фаззер ленты, только фаззер
    /// ходит между РАЗНЫМИ позициями и мимо этой развилки проскакивал.
    /// </summary>
    [Fact]
    public void Clicking_the_current_history_row_releases_the_pickup()
    {
        var doc = Painted();
        PickupOps.PromoteRect(doc, new SKRect(10, 10, 50, 50));

        doc.History.JumpTo(doc.History.Cursor, doc);

        Assert.Null(doc.FloatingPickup);
    }

    [Fact]
    public void The_current_row_and_the_way_round_give_the_same_document()
    {
        var doc = Painted();
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Blue, ToolSize = 4f, Opacity = 1f };
        Stroke(doc, ctx, 5, 5);

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 50, 50));
        doc.History.JumpTo(doc.History.Cursor, doc);      // щёлкнули по текущей строке
        bool afterCurrent = doc.FloatingPickup is null;

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 50, 50));
        doc.History.JumpTo(0, doc);                       // ушли и вернулись
        doc.History.JumpTo(1, doc);
        bool afterRound = doc.FloatingPickup is null;

        Assert.Equal(afterCurrent, afterRound);
    }

    /// <summary>
    /// Картинку, которую положила в руки сама лента (вставка), щелчок по её строке не
    /// снимает: снять её вправе только её собственная запись, иначе история утверждала бы,
    /// что вставка применена, а на холсте её нет.
    /// </summary>
    [Fact]
    public void Clicking_the_row_of_a_paste_keeps_the_pasted_picture()
    {
        var vm = new MainViewModel();
        using var img = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(img)) c.Clear(SKColors.Blue);
        vm.PasteBitmap(img);

        vm.Document.History.JumpTo(vm.Document.History.Cursor, vm.Document);

        Assert.NotNull(vm.Document.FloatingPickup);
    }

    // ───────── слой, выкрученный в полную прозрачность ─────────

    /// <summary>
    /// Правило «спрятать то, что пользователь сейчас тащит, хуже любой нестыковки» было
    /// записано для видимости слоя и забыто для прозрачности: объект, поднятый со слоя,
    /// чей ползунок увели в ноль, пропадал с экрана целиком - рамка и ручки на месте, а
    /// внутри пусто.
    /// </summary>
    [Fact]
    public void A_pickup_stays_visible_on_a_zero_opacity_layer()
    {
        var doc = Painted();
        PickupOps.PromoteRect(doc, new SKRect(10, 10, 50, 50));
        doc.Layers[0].Opacity = 0f;

        using var flat = FileService.Flatten(doc);
        var px = flat.GetPixel(30, 30);

        Assert.True(px.Red > 200 && px.Green < 80, $"объект в руках не виден: {px}");
    }

    [Fact]
    public void A_pickup_stays_visible_on_a_hidden_layer()
    {
        var doc = Painted();
        PickupOps.PromoteRect(doc, new SKRect(10, 10, 50, 50));
        doc.Layers[0].Visible = false;

        using var flat = FileService.Flatten(doc);
        var px = flat.GetPixel(30, 30);

        Assert.True(px.Red > 200 && px.Green < 80, $"объект в руках не виден: {px}");
    }

    /// <summary>Пипетка и статусбар обязаны отвечать тем же, что показывает экран.</summary>
    [Fact]
    public void Sampling_agrees_with_the_screen_over_a_zero_opacity_layer()
    {
        var doc = Painted();
        PickupOps.PromoteRect(doc, new SKRect(10, 10, 50, 50));
        doc.Layers[0].Opacity = 0f;

        using var flat = FileService.Flatten(doc);
        var sampled = doc.SampleComposite(30, 30);
        var drawn = flat.GetPixel(30, 30);

        Assert.InRange(Math.Abs(sampled.Red - drawn.Red), 0, 2);
        Assert.InRange(Math.Abs(sampled.Green - drawn.Green), 0, 2);
        Assert.InRange(Math.Abs(sampled.Blue - drawn.Blue), 0, 2);
    }

    /// <summary>
    /// А сам слой с нулевой прозрачностью не должен подкрашивать картинку вовсе. Skia на
    /// краске с нулевой альфой всё равно подмешивает битмап примерно на один уровень из
    /// 255: белая бумага под таким слоем выходила #FEFEFE вместо #FFFFFF - и на экране, и
    /// в сохранённом файле. Пипетка при этом отвечала честным #FFFFFF, и два места,
    /// которые обязаны отвечать одинаково, расходились на ровном месте.
    /// </summary>
    [Fact]
    public void A_zero_opacity_layer_does_not_tint_the_picture()
    {
        var doc = new Document(30, 30);
        doc.Layers.Add(new PixelLayer(30, 30, SKColors.Green) { Opacity = 0f });

        using var flat = FileService.Flatten(doc);
        var px = flat.GetPixel(15, 15);

        Assert.Equal(255, px.Red);
        Assert.Equal(255, px.Green);
        Assert.Equal(255, px.Blue);
    }

    [Fact]
    public void A_hidden_layer_does_not_tint_the_picture()
    {
        var doc = new Document(30, 30);
        doc.Layers.Add(new PixelLayer(30, 30, SKColors.Green) { Visible = false });

        using var flat = FileService.Flatten(doc);
        var px = flat.GetPixel(15, 15);

        Assert.Equal(255, px.Red);
        Assert.Equal(255, px.Green);
        Assert.Equal(255, px.Blue);
    }

    /// <summary>Полупрозрачный слой при этом обязан рисоваться как и прежде.</summary>
    [Fact]
    public void A_half_transparent_layer_still_shows()
    {
        var doc = new Document(30, 30);
        doc.Layers.Add(new PixelLayer(30, 30, SKColors.Black) { Opacity = 0.5f });

        using var flat = FileService.Flatten(doc);
        var px = flat.GetPixel(15, 15);

        Assert.InRange(px.Red, 100, 160);
    }
}
