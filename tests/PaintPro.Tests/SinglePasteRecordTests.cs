using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Лента истории и плавающий объект: позиция курсора обязана однозначно задавать
/// документ, а объект, которого пользователь держит в руках, не должен пропадать молча.
/// </summary>
public class SinglePasteRecordTests
{
    private static PixelLayer Paper(Document d) => (PixelLayer)d.Layers[0];

    private static SKBitmap Img(int w, int h, SKColor c)
    {
        var b = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(b);
        canvas.Clear(c);
        return b;
    }

    private static void Rect(PixelLayer l, float x0, float y0, float x1, float y1, SKColor c)
    {
        using var canvas = new SKCanvas(l.Bitmap);
        using var p = new SKPaint { Color = c, BlendMode = SKBlendMode.Src, IsAntialias = false };
        canvas.DrawRect(new SKRect(x0, y0, x1, y1), p);
    }

    /// <summary>
    /// Клик по строке «Вставка» в панели истории обязан вернуть картинку в руки, а не
    /// стереть её.
    ///
    /// Прогулка по ленте снимала плавающий объект без разбора - и тот, что поднял
    /// пользователь, и тот, что положила в руки вставка. Первый снимать надо: он держит
    /// снимок слоя на момент подъёма, а после шага по ленте этот снимок описывает
    /// состояние, которого больше нет. Второму же в ленте соответствует своя запись, и
    /// пока она применена, картинка обязана быть на экране. Пользователь кликал по строке
    /// «Вставка», картинка исчезала, и вернуть её было нечем: курсор уже стоял там, куда
    /// его просили поставить.
    /// </summary>
    [Fact]
    public void Jumping_onto_the_paste_row_keeps_the_picture_in_hand()
    {
        var doc = new Document(60, 40);
        using var img = Img(10, 10, SKColors.Red);
        doc.History.ExecuteAndPush(new PasteCommand(img, new SKPoint(5, 5)), doc);
        doc.History.ExecuteAndPush(new LayerPropertyCommand(doc.Layers[0], true, 0.5f), doc);

        doc.History.JumpTo(1, doc);

        Assert.NotNull(doc.FloatingPickup);
        Assert.Equal(1, doc.History.Cursor);
    }

    /// <summary>То же самое и для обычного Ctrl+Y.</summary>
    [Fact]
    public void Redo_keeps_a_pasted_picture_in_hand()
    {
        var doc = new Document(60, 40);
        using var img = Img(10, 10, SKColors.Red);
        doc.History.ExecuteAndPush(new PasteCommand(img, new SKPoint(5, 5)), doc);
        doc.History.ExecuteAndPush(new LayerPropertyCommand(doc.Layers[0], true, 0.5f), doc);

        doc.History.Undo(doc);
        Assert.NotNull(doc.FloatingPickup);
        doc.History.Redo(doc);
        Assert.NotNull(doc.FloatingPickup);
    }

    /// <summary>
    /// А поднятое пользователем выделение прогулка по-прежнему возвращает на место: его
    /// снимок после шага по ленте уже ни к чему не относится.
    /// </summary>
    [Fact]
    public void A_user_lift_is_still_put_back_by_a_timeline_walk()
    {
        var doc = new Document(60, 40);
        Rect(Paper(doc), 8, 8, 28, 24, SKColors.Red);
        var before = Paper(doc).ExtractRegion(new SKRectI(0, 0, 60, 40));
        doc.History.ExecuteAndPush(new LayerPropertyCommand(doc.Layers[0], true, 0.5f), doc);

        PickupOps.PromoteRect(doc, new SKRect(8, 8, 28, 24));
        PickupOps.EnsureLazyErase(doc, doc.FloatingPickup!);
        PickupOps.Translate(doc.FloatingPickup!, 10, 5);

        doc.History.JumpTo(0, doc);
        doc.History.JumpTo(1, doc);

        Assert.Null(doc.FloatingPickup);
        int diff = 0;
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 60; x++)
                if (before.GetPixel(x, y) != Paper(doc).Bitmap.GetPixel(x, y)) diff++;
        Assert.Equal(0, diff);
    }

    /// <summary>
    /// Прижатие, не изменившее ни одного пикселя, записи не оставляет.
    ///
    /// Пустой габарит отсеивался и раньше, а вот пустая правка в непустом габарите - нет.
    /// Картинку вставляли на холст и уносили за его край: рисовать нечего, но габарит
    /// правки накрывал место, где она лежала вначале, и в ленту уходил диф, у которого
    /// «до» и «после» побайтно одинаковы. Пользователь получал вторую строку «Вставка»,
    /// не делающую ровно ничего.
    /// </summary>
    [Fact]
    public void A_commit_that_changed_nothing_records_nothing()
    {
        var doc = new Document(60, 40);
        using var img = Img(10, 10, SKColors.Red);
        doc.History.ExecuteAndPush(new PasteCommand(img, new SKPoint(5, 5)), doc);
        PickupOps.Translate(doc.FloatingPickup!, -200, -200);

        doc.CommitFloating();

        Assert.Null(doc.FloatingPickup);
        Assert.Empty(doc.History.Commands);
        Assert.False(doc.History.IsDirtySinceSave);
    }

    /// <summary>
    /// Второй плавающий объект прижимает первый, а не затирает его.
    ///
    /// Присваивание в <see cref="Document.FloatingPickup"/> просто меняло ссылку: пиксели
    /// первого объекта не ложились ни на один слой и пропадали совсем, его битмапы не
    /// освобождались вовсе (нативная память мимо сборщика мусора), а если это была
    /// вставка - её запись оставалась в ленте применённой при том, что картинки на холсте
    /// уже нет. После этого позиция курсора переставала однозначно задавать документ:
    /// пройдя ленту снизу, на той же строке получали картинку в руках, пройдя сверху -
    /// пустоту. Нашёл это тяжёлый прогон фаззера на 1200 сценариях.
    ///
    /// Правило то же, что у рамки выделения поверх объекта, и живёт там же - в самом
    /// документе, а не в каждом вызывающем по отдельности.
    /// </summary>
    [Fact]
    public void A_second_pickup_commits_the_first_instead_of_dropping_it()
    {
        var doc = new Document(120, 90);
        Rect(Paper(doc), 10, 10, 30, 30, SKColors.Red);
        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));
        PickupOps.EnsureLazyErase(doc, doc.FloatingPickup!);
        PickupOps.Translate(doc.FloatingPickup!, 40, 20);

        PickupOps.PromoteRect(doc, new SKRect(80, 60, 100, 80));

        // Первый лёг на холст на новом месте и отменяем.
        Assert.Equal(SKColors.Red, Paper(doc).Bitmap.GetPixel(55, 35));
        Assert.NotNull(doc.History.Current);
        Assert.Equal("Перемещение", doc.History.Current!.DisplayName);
    }

    /// <summary>Вставка поверх поднятого объекта прижимает его тем же правилом.</summary>
    [Fact]
    public void A_paste_over_a_lift_commits_the_lift()
    {
        var doc = new Document(120, 90);
        Rect(Paper(doc), 10, 10, 30, 30, SKColors.Red);
        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));
        PickupOps.EnsureLazyErase(doc, doc.FloatingPickup!);
        PickupOps.Translate(doc.FloatingPickup!, 40, 20);

        using var img = Img(8, 8, SKColors.Blue);
        doc.History.ExecuteAndPush(new PasteCommand(img, new SKPoint(5, 5)), doc);

        Assert.Equal(SKColors.Red, Paper(doc).Bitmap.GetPixel(55, 35));
        Assert.Equal(2, doc.History.Commands.Count);
    }

    /// <summary>
    /// Отмена и повтор прижатой вставки ходят по холсту одним шагом в каждую сторону.
    /// </summary>
    [Fact]
    public void Undo_and_redo_of_a_committed_paste_are_one_step_each()
    {
        var vm = new MainViewModel();
        var doc = vm.Document;
        using var img = Img(10, 10, SKColors.Red);
        vm.PasteBitmap(img);
        PickupOps.Translate(doc.FloatingPickup!, 30, 30);
        vm.CommitFloatingCommand.Execute(null);

        Assert.Single(doc.History.Commands);
        Assert.Equal(SKColors.Red, Paper(doc).Bitmap.GetPixel(55, 55));

        vm.UndoCommand.Execute(null);
        Assert.Equal(SKColors.White, Paper(doc).Bitmap.GetPixel(55, 55));
        Assert.Null(doc.FloatingPickup);

        vm.RedoCommand.Execute(null);
        Assert.Equal(SKColors.Red, Paper(doc).Bitmap.GetPixel(55, 55));
        Assert.Null(doc.FloatingPickup);
    }
}
