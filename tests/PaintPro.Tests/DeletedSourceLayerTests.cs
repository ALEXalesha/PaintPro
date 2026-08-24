using PaintPro.Models;
using PaintPro.Services;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Слой, с которого подняли пиксели, удалили, пока объект ещё в руках.
///
/// Пикап помнит слой по идентификатору и держит снимок этого слоя на момент подъёма.
/// Когда слой пропадает, поиск по идентификатору возвращает пустоту, и прежний код
/// подставлял вместо него активный слой - всюду, включая места, где речь идёт о
/// ПРЕЖНЕМ состоянии. Снимок чужого слоя уходил в активный: Escape стирал по нему
/// рисунок целиком, Delete записывал в историю дыру не там, где надо, а первый же
/// Ctrl+Z после прижатия вписывал в активный слой пиксели того, чего в документе
/// больше нет.
///
/// Пиксели при этом терять незачем - объект в руках у пользователя, и прижать его надо
/// туда, куда он его несёт. Разделены поэтому две вещи: КУДА рисовать (запасной
/// вариант допустим) и ЧЕЙ снимок описывает «до» (только слой-источник).
/// </summary>
public class DeletedSourceLayerTests
{
    private static SKBitmap Snap(PixelLayer l) => l.ExtractRegion(new SKRectI(0, 0, l.Width, l.Height));

    private static int Diff(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return int.MaxValue;
        int n = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) n++;
        return n;
    }

    /// <summary>Бумага с зелёным рисунком + верхний слой с синим квадратом 10..30.</summary>
    private static Document MakeDoc(out PixelLayer paper)
    {
        var doc = new Document(60, 60);
        doc.Layers.Add(new PixelLayer(60, 60, SKColors.Transparent) { Name = "Верхний" });
        doc.ActiveLayerIndex = 1;
        using (var c = new SKCanvas(((PixelLayer)doc.Layers[1]).Bitmap))
            c.DrawRect(new SKRect(10, 10, 30, 30), new SKPaint { Color = SKColors.Blue });

        paper = (PixelLayer)doc.Layers[0];
        using (var c = new SKCanvas(paper.Bitmap))
            c.DrawRect(new SKRect(0, 0, 60, 60), new SKPaint { Color = SKColors.Green });
        return doc;
    }

    /// <summary>Удалить верхний слой, не трогая историю: так делает и сам пользователь.</summary>
    private static void DropTopLayer(Document doc)
    {
        doc.Layers[1].Dispose();
        doc.Layers.RemoveAt(1);
        doc.ActiveLayerIndex = 0;
    }

    [Fact]
    public void Undo_of_a_commit_does_not_paste_the_deleted_layer_over_the_paper()
    {
        var doc = MakeDoc(out var paper);
        var before = Snap(paper);

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));
        var fp = doc.FloatingPickup!;
        PickupOps.EnsureLazyErase(doc, fp);
        PickupOps.Translate(fp, 5, 5);

        DropTopLayer(doc);
        doc.CommitFloating();
        doc.History.Undo(doc);

        // Отмена обязана вернуть бумагу ровно к тому, что на ней было. Прежде «до»
        // бралось из снимка удалённого слоя, и на бумагу ложился прозрачный
        // прямоугольник с синим квадратом внутри.
        Assert.Equal(0, Diff(before, Snap(paper)));
    }

    [Fact]
    public void Escape_does_not_paste_the_deleted_layer_over_the_paper()
    {
        var doc = MakeDoc(out var paper);
        var before = Snap(paper);

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));
        var fp = doc.FloatingPickup!;
        PickupOps.EnsureLazyErase(doc, fp);
        PickupOps.Translate(fp, 5, 5);

        DropTopLayer(doc);
        doc.CancelFloating();

        Assert.Equal(0, Diff(before, Snap(paper)));
    }

    [Fact]
    public void Delete_does_not_punch_a_hole_in_the_paper()
    {
        var doc = MakeDoc(out var paper);
        var before = Snap(paper);

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));

        DropTopLayer(doc);
        doc.DiscardFloating();

        Assert.Equal(0, Diff(before, Snap(paper)));
    }

    /// <summary>
    /// Ленивое стирание исходной области - тоже про слой-источник. Пока оно подставляло
    /// активный слой, первое же перемещение объекта, чей слой удалили, вырезало дыру в
    /// бумаге по координатам, к ней никак не относящимся.
    /// </summary>
    [Fact]
    public void Lazy_erase_does_not_cut_into_another_layer()
    {
        var doc = MakeDoc(out var paper);
        var before = Snap(paper);

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));
        var fp = doc.FloatingPickup!;

        DropTopLayer(doc);
        PickupOps.EnsureLazyErase(doc, fp);

        Assert.Equal(0, Diff(before, Snap(paper)));
    }

    /// <summary>
    /// Пиксели при этом не пропадают: объект прижимается в тот слой, который активен,
    /// и снимается обычным Ctrl+Z.
    /// </summary>
    [Fact]
    public void The_lifted_pixels_still_land_somewhere_and_stay_undoable()
    {
        var doc = MakeDoc(out var paper);

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));
        var fp = doc.FloatingPickup!;
        PickupOps.EnsureLazyErase(doc, fp);
        PickupOps.Translate(fp, 5, 5);

        DropTopLayer(doc);
        doc.CommitFloating();

        Assert.Equal(SKColors.Blue, paper.Bitmap.GetPixel(20, 20));
        Assert.Equal(1, doc.History.Cursor);
    }
}
