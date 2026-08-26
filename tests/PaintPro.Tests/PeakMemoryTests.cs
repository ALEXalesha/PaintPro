using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace PaintPro.Tests;

/// <summary>
/// Сколько КАЖДАЯ операция просит сверх самого документа.
///
/// У документа потолок есть - сторона до 20000 пикселей и до 120 млн пикселей всего, - но
/// отдельные операции заводят под себя не одну такую площадь, а несколько, и на это не
/// смотрел никто. Тот же шаблон, что уронил приложение на масштабе в 1.21.0: потолок стоит
/// на том, что хранится, и не стоит на том, что из этого разворачивается по ходу работы.
///
/// Меряется управляемая память - поточный счётчик выделений точен и повторяем.
/// Нативные битмапы Skia в этот счёт не входят, и померить их отсюда нечем:
/// счётчики процесса на таких объёмах шумят сильнее самой разницы. Поэтому здесь только
/// те операции, чьи крупные буферы управляемые, - заливка и подъём выделения; остальное
/// ограничивается счётом (см. <see cref="LayerStackCommand.MaxLayersFor"/>).
/// </summary>
public class PeakMemoryTests
{
    private readonly ITestOutputHelper _out;
    public PeakMemoryTests(ITestOutputHelper output) => _out = output;

    /// <summary>Сторона пробного холста: достаточно большая, чтобы буферы было видно за шумом.</summary>
    private const int Side = 2000;

    /// <summary>
    /// Управляемые выделения за один вызов, в байтах.
    ///
    /// Счётчик именно ПОТОЧНЫЙ, а не общий по процессу: xunit гоняет классы тестов
    /// параллельно, и <see cref="GC.GetTotalAllocatedBytes"/> считал бы заодно всё, что
    /// выделяют соседи. Из-за этого проверка проходила в одиночку и падала в общем прогоне.
    /// </summary>
    private static long Managed(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static Document Painted()
    {
        var doc = new Document(Side, Side);
        using var c = new SKCanvas(((PixelLayer)doc.Layers[0]).Bitmap);
        c.Clear(SKColors.White);
        c.DrawRect(new SKRect(Side * 0.2f, Side * 0.2f, Side * 0.8f, Side * 0.8f),
                   new SKPaint { Color = SKColors.Red });
        return doc;
    }

    /// <summary>Во сколько раз выделения превышают сам холст (по четыре байта на пиксель).</summary>
    private double Ratio(string what, Action<Document> operation)
    {
        // Документ готовим ДО замера: считаем стоимость самой операции, а не подготовки.
        var doc = Painted();
        long canvas = (long)Side * Side * 4;
        long allocated = Managed(() => operation(doc));
        double ratio = allocated / (double)canvas;
        _out.WriteLine($"{what}: холст {canvas / 1024 / 1024} МБ, выделено {allocated / 1024 / 1024} МБ = x{ratio:F2}");
        return ratio;
    }

    /// <summary>
    /// Заливке нужен один буфер размером с холст - в нём она и работает. Второй, копия
    /// всего слоя «на всякий случай», из которой потом вырезался снимок для отмены, был
    /// лишним: слой всё это время держит прежние пиксели сам, и снимок берётся с него,
    /// когда область уже известна. На холсте в 120 млн пикселей это 480 сэкономленных
    /// мегабайт на каждый щелчок.
    /// </summary>
    [Fact]
    public void A_fill_needs_one_canvas_worth_of_buffer()
    {
        double r = Ratio("заливка", doc =>
        {
            var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Blue, Opacity = 1f };
            new FillTool().OnPointerDown(new SKPoint(10, 10), ctx);
        });
        Assert.True(r < 1.2, $"заливка просит x{r:F2} от холста");
    }

    /// <summary>
    /// Подъём выделения во весь холст выкусывает из него фон, а это обход по всей площади.
    /// Пока пиксель помечался пройденным при извлечении из стека, а не при помещении в
    /// него, один и тот же индекс попадал в стек по числу соседей: подъём просил пять с
    /// половиной размеров картинки.
    /// </summary>
    [Fact]
    public void Lifting_the_whole_canvas_does_not_pile_up_the_stack()
    {
        double r = Ratio("подъём всего холста",
            doc => PickupOps.PromoteRect(doc, new SKRect(0, 0, doc.CanvasWidth, doc.CanvasHeight)));
        Assert.True(r < 2.0, $"подъём просит x{r:F2} от холста");
    }

    /// <summary>Короткий штрих не имеет права стоить как весь холст.</summary>
    [Fact]
    public void A_short_stroke_costs_little()
    {
        double r = Ratio("короткий штрих", doc =>
        {
            var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Blue, ToolSize = 4, Opacity = 1f };
            var t = new BrushTool();
            t.OnPointerDown(new SKPoint(100, 100), ctx);
            t.OnPointerMove(new SKPoint(104, 104), ctx);
            t.OnPointerUp(new SKPoint(104, 104), ctx);
        });
        Assert.True(r < 0.2, $"штрих в четыре пикселя просит x{r:F2} от холста");
    }

    /// <summary>Заливка по-прежнему заливает и по-прежнему отменяется до пикселя.</summary>
    [Fact]
    public void The_leaner_fill_still_fills_and_still_undoes()
    {
        var doc = new Document(60, 60);
        var pl = (PixelLayer)doc.Layers[0];
        using (var c = new SKCanvas(pl.Bitmap))
            c.DrawRect(new SKRect(10, 10, 50, 50), new SKPaint { Color = SKColors.Red });
        var before = pl.ExtractRegion(new SKRectI(0, 0, 60, 60));

        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Blue, Opacity = 1f };
        new FillTool().OnPointerDown(new SKPoint(30, 30), ctx);
        Assert.Equal(SKColors.Blue.Blue, pl.Bitmap.GetPixel(30, 30).Blue);

        doc.History.Undo(doc);
        var after = pl.ExtractRegion(new SKRectI(0, 0, 60, 60));
        Assert.True(before.GetPixelSpan().SequenceEqual(after.GetPixelSpan()));
    }

    /// <summary>И повтор заливки возвращает её обратно.</summary>
    public static IEnumerable<object[]> FillSeeds => new[] { new object[] { 5, 5 }, new object[] { 30, 30 } };

    [Theory]
    [MemberData(nameof(FillSeeds))]
    public void The_leaner_fill_redoes(int x, int y)
    {
        var doc = new Document(60, 60);
        var pl = (PixelLayer)doc.Layers[0];
        using (var c = new SKCanvas(pl.Bitmap))
            c.DrawRect(new SKRect(10, 10, 50, 50), new SKPaint { Color = SKColors.Red });

        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Blue, Opacity = 1f };
        new FillTool().OnPointerDown(new SKPoint(x, y), ctx);
        var filled = pl.ExtractRegion(new SKRectI(0, 0, 60, 60));

        doc.History.Undo(doc);
        doc.History.Redo(doc);

        var again = ((PixelLayer)doc.Layers[0]).ExtractRegion(new SKRectI(0, 0, 60, 60));
        Assert.True(filled.GetPixelSpan().SequenceEqual(again.GetPixelSpan()));
    }

    /// <summary>Выкусывание фона по-прежнему не пробивает дыр в самом рисунке.</summary>
    [Fact]
    public void The_leaner_keying_still_keeps_the_highlights()
    {
        var src = new SKBitmap(30, 30, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(src))
        {
            c.Clear(SKColors.White);
            c.DrawRect(new SKRect(5, 5, 25, 25), new SKPaint { Color = SKColors.Black });
            c.DrawRect(new SKRect(12, 12, 18, 18), new SKPaint { Color = SKColors.White });
        }
        using var keyed = BitmapKeying.KeyOutBackground(src, SKColors.White);

        Assert.Equal(0, keyed.GetPixel(1, 1).Alpha);        // фон снаружи ушёл
        Assert.Equal(255, keyed.GetPixel(15, 15).Alpha);    // блик внутри остался
        Assert.Equal(255, keyed.GetPixel(8, 8).Alpha);      // сам рисунок на месте
        src.Dispose();
    }

    // ───────── потолок на число слоёв ─────────

    /// <summary>
    /// На обычном холсте предел не мешает: сотня слоёв - это больше, чем кто-либо заводит.
    /// </summary>
    [Fact]
    public void A_normal_canvas_allows_plenty_of_layers()
    {
        Assert.Equal(LayerStackCommand.MaxLayerCount,
            LayerStackCommand.MaxLayersFor(Document.DefaultWidth, Document.DefaultHeight));
    }

    /// <summary>А на большом - предел считается по площади и становится тесным.</summary>
    [Fact]
    public void A_huge_canvas_allows_few_layers()
    {
        int photo = LayerStackCommand.MaxLayersFor(4000, 3000);
        int huge = LayerStackCommand.MaxLayersFor(12000, 10000);
        Assert.InRange(photo, 2, 30);
        Assert.InRange(huge, 1, 2);
    }

    /// <summary>Упёршись в предел, «Добавить слой» объясняет отказ, а не молчит.</summary>
    [Fact]
    public void Refusing_to_add_a_layer_says_why()
    {
        var vm = new MainViewModel();
        vm.Document.History.ExecuteAndPush(new ResizeCanvasCommand(12000, 10000), vm.Document);
        int before = vm.Document.Layers.Count;

        vm.AddLayerCommand.Execute(null);

        Assert.Equal(before, vm.Document.Layers.Count);
        Assert.NotEqual("", vm.StatusHint);
    }

    /// <summary>А на обычном холсте слой заводится как и раньше.</summary>
    [Fact]
    public void A_layer_is_still_added_on_a_normal_canvas()
    {
        var vm = new MainViewModel();
        vm.AddLayerCommand.Execute(null);
        Assert.Equal(2, vm.Document.Layers.Count);
        Assert.Equal("", vm.StatusHint);
    }
}
