using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Фаззер признака несохранённой работы. После «здесь сохранено» делаем случайные правки и
/// на каждом шаге сверяем «изменено» со сборкой документа.
///
/// Инвариант односторонний, и это важно. «Картинка отличается от сохранённой ⇒ документ
/// изменён» - закон: обратное означало бы молча потерянную работу при закрытии окна.
/// А вот «изменён ⇒ картинка отличается» законом не является, и требовать его нельзя:
/// добавленный слой сборку не меняет (файл-то плоский), но потерять стопку слоёв
/// пользователь не соглашался; два отражения подряд возвращают холст на место, а курсор
/// ленты стоит уже не там, где метка сохранения.
///
/// Пустую правку, от которой картинка не меняется ВООБЩЕ, ловит вторая проверка ниже -
/// точечно и без случайностей.
/// </summary>
public class DirtyFlagFuzzTests
{
    private static SKBitmap Flat(Document doc) => FileService.Flatten(doc);

    private static bool Same(SKBitmap a, SKBitmap b)
        => a.Width == b.Width && a.Height == b.Height
           && a.GetPixelSpan().SequenceEqual(b.GetPixelSpan());

    /// <summary>Один случайный шаг: рисующие инструменты, операции над документом и прогулка по ленте.</summary>
    private static void RandomEdit(Random rnd, Document doc, ToolContext ctx)
    {
        float X() => (float)(rnd.NextDouble() * doc.CanvasWidth);
        ctx.PrimaryColor = new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
        ctx.Opacity = 0.3f + (float)rnd.NextDouble() * 0.7f;
        ctx.ToolSize = 1 + rnd.Next(6);

        switch (rnd.Next(14))
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
                var t = new EraserTool();
                t.OnPointerDown(new SKPoint(X(), X()), ctx);
                t.OnPointerMove(new SKPoint(X(), X()), ctx);
                t.OnPointerUp(new SKPoint(X(), X()), ctx);
                break;
            }
            case 2:
                new FillTool().OnPointerDown(new SKPoint(X(), X()), ctx);
                break;
            case 3:
            {
                var t = new RectShapeTool { Fill = true };
                t.OnPointerDown(new SKPoint(X(), X()), ctx);
                t.OnPointerMove(new SKPoint(X(), X()), ctx);
                t.OnPointerUp(new SKPoint(X(), X()), ctx);
                break;
            }
            case 4:
            {
                var t = new TextTool();
                t.OnPointerDown(new SKPoint(X(), X()), ctx);
                t.CommitText("Аб");
                break;
            }
            case 5:
                doc.CommitFloating();
                doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, $"L{doc.Layers.Count}"), doc);
                break;
            case 6:
                doc.CommitFloating();
                doc.History.ExecuteAndPush(DocumentTransform.Flip(doc, rnd.Next(2) == 0), doc);
                break;
            case 7:
                doc.CommitFloating();
                doc.History.ExecuteAndPush(new ResizeCanvasCommand(15 + rnd.Next(30), 15 + rnd.Next(30)), doc);
                break;
            case 8:
            {
                float x = X(), y = X();
                PickupOps.PromoteRect(doc, new SKRect(x, y, x + 10, y + 10));
                if (doc.FloatingPickup is { } fp)
                {
                    PickupOps.EnsureLazyErase(doc, fp);
                    PickupOps.Translate(fp, 5, 5);
                    doc.CommitFloating();
                }
                break;
            }
            case 9:
            {
                var layer = doc.Layers[rnd.Next(doc.Layers.Count)];
                doc.History.ExecuteAndPush(
                    new LayerPropertyCommand(layer, rnd.Next(4) != 0, (float)rnd.NextDouble()), doc);
                break;
            }
            case 10: if (doc.History.CanUndo) doc.History.Undo(doc); break;
            case 11: if (doc.History.CanRedo) doc.History.Redo(doc); break;
            case 12: doc.History.JumpTo(rnd.Next(doc.History.Commands.Count + 1), doc); break;
            case 13:
            {
                doc.CommitFloating();
                var b = SKRectI.Intersect(
                    SKRectI.Round(new SKRect(X(), X(), X() + 12, X() + 9)),
                    new SKRectI(0, 0, doc.CanvasWidth, doc.CanvasHeight));
                if (b.HasArea())
                {
                    var cmd = new EraseRegionCommand(b, null, PickupOps.EraseColor(doc, doc.ActiveLayer));
                    doc.History.ExecuteAndPush(cmd, doc);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Закон: картинка отличается от сохранённой ⇒ приложение обязано считать документ
    /// изменённым. Иначе окно закроется молча и работа пропадёт.
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    public void A_changed_picture_is_never_reported_as_saved(int seed)
    {
        var rnd = new Random(seed * 104729);
        var vm = new MainViewModel();
        var doc = vm.Document;
        var ctx = vm.ToolContext;

        for (int i = 0; i < 3; i++)
        {
            doc.ActiveLayerIndex = rnd.Next(doc.Layers.Count);
            RandomEdit(rnd, doc, ctx);
        }
        doc.CommitFloating();
        doc.History.MarkSaved();
        using var saved = Flat(doc);

        var problems = new List<string>();
        for (int step = 0; step < 12; step++)
        {
            doc.ActiveLayerIndex = rnd.Next(doc.Layers.Count);
            RandomEdit(rnd, doc, ctx);

            using var now = Flat(doc);
            if (!Same(saved, now) && !vm.IsDirty)
                problems.Add($"шаг {step}: картинка другая, а документ объявлен сохранённым");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// Обратная сторона, точечно: жест рисующим инструментом, не изменивший ни одного
    /// пикселя, не имеет права включать признак несохранённой работы. Ровно этим болели
    /// ластик по чистой бумаге, белая кисть по белому и штрих мимо холста.
    /// </summary>
    [Theory]
    [InlineData("eraser")]
    [InlineData("white-brush")]
    [InlineData("white-shape")]
    [InlineData("white-text")]
    [InlineData("offscreen")]
    public void An_empty_gesture_never_dirties_a_saved_document(string gesture)
    {
        var vm = new MainViewModel();
        var doc = vm.Document;
        var ctx = vm.ToolContext;
        ctx.ToolSize = 8f;
        ctx.Opacity = 1f;
        doc.History.MarkSaved();

        switch (gesture)
        {
            case "eraser":
            {
                var t = new EraserTool();
                t.OnPointerDown(new SKPoint(40, 40), ctx);
                t.OnPointerMove(new SKPoint(80, 80), ctx);
                t.OnPointerUp(new SKPoint(80, 80), ctx);
                break;
            }
            case "white-brush":
            {
                ctx.PrimaryColor = SKColors.White;
                var t = new BrushTool();
                t.OnPointerDown(new SKPoint(40, 40), ctx);
                t.OnPointerMove(new SKPoint(80, 80), ctx);
                t.OnPointerUp(new SKPoint(80, 80), ctx);
                break;
            }
            case "white-shape":
            {
                ctx.PrimaryColor = SKColors.White;
                var t = new RectShapeTool { Fill = true };
                t.OnPointerDown(new SKPoint(40, 40), ctx);
                t.OnPointerMove(new SKPoint(120, 120), ctx);
                t.OnPointerUp(new SKPoint(120, 120), ctx);
                break;
            }
            case "white-text":
            {
                ctx.PrimaryColor = SKColors.White;
                var t = new TextTool();
                t.OnPointerDown(new SKPoint(40, 60), ctx);
                t.CommitText("Пустые чернила");
                break;
            }
            case "offscreen":
            {
                ctx.PrimaryColor = SKColors.Red;
                var t = new BrushTool();
                t.OnPointerDown(new SKPoint(-300, -300), ctx);
                t.OnPointerMove(new SKPoint(-280, -280), ctx);
                t.OnPointerUp(new SKPoint(-280, -280), ctx);
                break;
            }
        }

        Assert.False(vm.IsDirty, $"жест «{gesture}» объявил документ изменённым");
        Assert.Empty(doc.History.Commands);
    }
}
