using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Два инварианта ленты истории на случайных последовательностях правок:
/// отмена всего возвращает документ к первому состоянию, а прогулка «в начало и обратно»
/// приводит его ровно туда же, куда его привели сами правки.
///
/// Сценарии здесь одноразовые и берутся из seed'а: смысл не в конкретной последовательности,
/// а в том, что порядок правок выбран не автором теста. Именно так нашлись и фантомная
/// копия вставки после клика по строке истории, и падение на габарите без площади -
/// обе руками написанным сценарием не воспроизводились.
/// </summary>
public class TimelineFuzzTests
{
    private static SKBitmap Snap(PixelLayer l) => l.ExtractRegion(new SKRectI(0, 0, l.Width, l.Height));

    private static SKBitmap[] SnapAll(Document doc)
        => doc.Layers.OfType<PixelLayer>().Select(Snap).ToArray();

    private static int DiffCount(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return int.MaxValue;
        int n = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) n++;
        return n;
    }

    private static int DiffAll(SKBitmap[] a, SKBitmap[] b)
    {
        if (a.Length != b.Length) return int.MaxValue;
        int n = 0;
        for (int i = 0; i < a.Length; i++) n += DiffCount(a[i], b[i]);
        return n;
    }

    private static void RandomEdit(Random rnd, Document doc, ToolContext ctx)
    {
        float X() => (float)(rnd.NextDouble() * doc.CanvasWidth);
        ctx.PrimaryColor = new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
        ctx.Opacity = 0.3f + (float)rnd.NextDouble() * 0.7f;
        ctx.ToolSize = 1 + rnd.Next(8);

        switch (rnd.Next(15))
        {
            case 0:
            {
                var t = new BrushTool();
                t.OnPointerDown(new SKPoint(X(), X()), ctx);
                t.OnPointerMove(new SKPoint(X(), X()), ctx);
                t.OnPointerUp(new SKPoint(X(), X()), ctx);
                break;
            }
            case 1:
            {
                var t = new FillTool();
                t.OnPointerDown(new SKPoint(X(), X()), ctx);
                break;
            }
            case 2:
            {
                // Подъём прямоугольника, сдвиг и прижатие.
                float x = X(), y = X();
                PickupOps.PromoteRect(doc, new SKRect(x, y, x + 12, y + 12));
                if (doc.FloatingPickup is { } fp)
                {
                    PickupOps.EnsureLazyErase(doc, fp);
                    PickupOps.Translate(fp, X() - 20, X() - 20);
                    doc.CommitFloating();
                }
                break;
            }
            case 3:
            {
                // Подъём многоугольника с поворотом.
                float x = X(), y = X();
                PickupOps.PromoteQuad(doc, new[]
                {
                    new SKPoint(x, y), new SKPoint(x + 14, y + 2),
                    new SKPoint(x + 12, y + 15), new SKPoint(x - 1, y + 13),
                });
                if (doc.FloatingPickup is { } fp)
                {
                    PickupOps.EnsureLazyErase(doc, fp);
                    fp.SetRotation((float)(rnd.NextDouble() * 2 - 1));
                    doc.CommitFloating();
                }
                break;
            }
            case 4:
            {
                // Вставка: объект остаётся в руках, следующая правка его прижмёт.
                using var img = new SKBitmap(8, 8);
                using (var c = new SKCanvas(img)) c.Clear(ctx.PrimaryColor);
                doc.History.ExecuteAndPush(new PasteCommand(img, new SKPoint(X(), X())), doc);
                if (rnd.Next(2) == 0)
                {
                    PickupOps.Translate(doc.FloatingPickup!, 4, 4);
                    doc.CommitFloating();
                }
                break;
            }
            case 5:
                doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, $"Layer {doc.Layers.Count}"), doc);
                break;
            case 6:
                if (doc.Layers.Count > 1 && doc.Layers[^1] is PixelLayer top)
                {
                    doc.CommitFloating();
                    doc.History.ExecuteAndPush(LayerStackCommand.Remove(doc, top), doc);
                }
                break;
            case 7:
            {
                doc.CommitFloating();
                var t = new RectShapeTool { Fill = true };
                t.OnPointerDown(new SKPoint(X(), X()), ctx);
                t.OnPointerMove(new SKPoint(X(), X()), ctx);
                t.OnPointerUp(new SKPoint(X(), X()), ctx);
                break;
            }
            case 8:
                doc.CommitFloating();
                doc.History.ExecuteAndPush(DocumentTransform.Flip(doc, rnd.Next(2) == 0), doc);
                break;

            // Правки, меняющие размер холста и свойства слоёв: они переписывают стопку
            // целиком, и именно на них ломались бы недосмотры в записях, которые холста
            // касаются лишь косвенно.
            case 9:
                doc.CommitFloating();
                doc.History.ExecuteAndPush(
                    DocumentTransform.Rotate(doc, rnd.Next(2) == 0 ? MathF.PI / 2f : -MathF.PI / 2f), doc);
                break;
            case 10:
                doc.CommitFloating();
                doc.History.ExecuteAndPush(
                    new ResizeCanvasCommand(10 + rnd.Next(40), 10 + rnd.Next(40)), doc);
                break;
            case 11:
            {
                doc.CommitFloating();
                int left = rnd.Next(doc.CanvasWidth - 6), top2 = rnd.Next(doc.CanvasHeight - 6);
                var region = new SKRectI(
                    left, top2,
                    Math.Min(doc.CanvasWidth, left + 6 + rnd.Next(20)),
                    Math.Min(doc.CanvasHeight, top2 + 6 + rnd.Next(20)));
                if (region.HasArea())
                    doc.History.ExecuteAndPush(DocumentTransform.Crop(doc, region), doc);
                break;
            }
            case 12:
            {
                var layer = doc.Layers[rnd.Next(doc.Layers.Count)];
                var cmd = new LayerPropertyCommand(layer, rnd.Next(4) != 0, (float)rnd.NextDouble());
                if (cmd.ChangedAnything) doc.History.ExecuteAndPush(cmd, doc);
                break;
            }
            case 13:
            {
                doc.CommitFloating();
                float x = X(), y = X();
                var b = SKRectI.Intersect(
                    SKRectI.Round(new SKRect(x, y, x + 14, y + 11)),
                    new SKRectI(0, 0, doc.CanvasWidth, doc.CanvasHeight));
                if (b.HasArea())
                    doc.History.ExecuteAndPush(
                        new EraseRegionCommand(b, null, PickupOps.EraseColor(doc, doc.ActiveLayer)), doc);
                break;
            }
            case 14:
            {
                // Подъём, масштабирование ручкой, прижатие.
                float x = X(), y = X();
                PickupOps.PromoteRect(doc, new SKRect(x, y, x + 15, y + 15));
                if (doc.FloatingPickup is { } fp)
                {
                    PickupOps.EnsureLazyErase(doc, fp);
                    fp.ApplyResize(ResizeHandle.SE, new SKPoint(X(), X()));
                    doc.CommitFloating();
                }
                break;
            }
        }
    }

    private static Document Play(int seed, int steps = 14)
    {
        var rnd = new Random(seed);
        var doc = new Document(40, 40);
        var ctx = new ToolContext(doc);
        for (int i = 0; i < steps; i++)
        {
            doc.ActiveLayerIndex = rnd.Next(doc.Layers.Count);
            RandomEdit(rnd, doc, ctx);
        }
        doc.CommitFloating();
        return doc;
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)] [InlineData(20)]
    public void Undoing_everything_returns_the_document_to_a_blank_sheet(int seed)
    {
        var blank = SnapAll(new Document(40, 40));
        var doc = Play(seed);

        while (doc.History.CanUndo) doc.History.Undo(doc);
        doc.DropFloating();

        Assert.Single(doc.Layers);
        Assert.Equal(40, doc.CanvasWidth);
        Assert.Equal(40, doc.CanvasHeight);
        Assert.Equal(0, DiffAll(blank, SnapAll(doc)));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)] [InlineData(20)]
    public void Walking_the_timeline_to_zero_and_back_reproduces_the_document(int seed)
    {
        var doc = Play(seed);
        int target = doc.History.Cursor;
        var expected = SnapAll(doc);
        int layers = doc.Layers.Count;
        int w = doc.CanvasWidth, h = doc.CanvasHeight;

        doc.History.JumpTo(0, doc);
        doc.History.JumpTo(target, doc);

        Assert.Null(doc.FloatingPickup);
        Assert.Equal(layers, doc.Layers.Count);
        Assert.Equal(w, doc.CanvasWidth);
        Assert.Equal(h, doc.CanvasHeight);
        Assert.Equal(0, DiffAll(expected, SnapAll(doc)));
    }

    /// <summary>
    /// Третий инвариант: у слоя кроме пикселей есть имя, видимость и прозрачность, и
    /// прогулка по ленте обязана возвращать их так же точно.
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void Walking_the_timeline_reproduces_the_layer_settings(int seed)
    {
        var doc = Play(seed);
        int target = doc.History.Cursor;
        var props = doc.Layers.Select(l => (l.Name, l.Visible, l.Opacity)).ToArray();

        doc.History.JumpTo(0, doc);
        doc.History.JumpTo(target, doc);

        Assert.Equal(props, doc.Layers.Select(l => (l.Name, l.Visible, l.Opacity)).ToArray());
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Undoing_and_redoing_everything_reproduces_the_document(int seed)
    {
        var doc = Play(seed);
        var edited = SnapAll(doc);

        while (doc.History.CanUndo) doc.History.Undo(doc);
        while (doc.History.CanRedo) doc.History.Redo(doc);
        doc.CommitFloating();

        Assert.Equal(0, DiffAll(edited, SnapAll(doc)));
    }
}
