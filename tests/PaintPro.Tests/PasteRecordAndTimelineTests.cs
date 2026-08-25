using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Лента истории и то, что от неё зависит: запись вставки, от которой отказались, повтор
/// ленты через прижатие, копия поднятого объекта, Shift у ручек и подсказка про
/// прозрачность.
/// </summary>
public class PasteRecordAndTimelineTests
{
    private static SKBitmap Patch(SKColor color, int size = 16)
    {
        var bmp = new SKBitmap(size, size);
        using var c = new SKCanvas(bmp);
        c.Clear(color);
        return bmp;
    }

    private static void FillRect(PixelLayer layer, SKRect rect, SKColor color)
    {
        using var c = new SKCanvas(layer.Bitmap);
        using var p = new SKPaint { Color = color, BlendMode = SKBlendMode.Src };
        c.DrawRect(rect, p);
    }

    private static SKBitmap Snap(PixelLayer l) => l.ExtractRegion(new SKRectI(0, 0, l.Width, l.Height));

    private static int DiffCount(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return int.MaxValue;
        int n = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) n++;
        return n;
    }

    // ───────── вставка, от которой отказались не сразу ─────────

    [Fact]
    public void Escape_on_a_stale_paste_strikes_its_record_out_of_the_timeline()
    {
        var vm = new MainViewModel();
        using var img = Patch(SKColors.Red);
        vm.PasteBitmap(img);
        // Между вставкой и отказом от неё ложится другая правка - отменять её нельзя.
        var other = new LayerPropertyCommand(vm.Document.Layers[0], visible: true, opacity: 0.5f);
        vm.Document.History.ExecuteAndPush(other, vm.Document);

        vm.CancelFloatingCommand.Execute(null);

        // Пока запись «Вставка» оставалась в ленте, история утверждала, что вставка
        // применена, картинки на холсте не было, документ считался изменённым, а Ctrl+Y
        // картинку не возвращал: курсору некуда двигаться.
        Assert.Null(vm.Document.FloatingPickup);
        Assert.Equal(new[] { other }, vm.Document.History.Commands);
        Assert.False(vm.Document.History.CanRedo);
    }

    // Ctrl+X по такой же вставке проверяет CompositeAndEraseTests через Delete: сам
    // Ctrl+X идёт в системный буфер обмена, а тот работает только из STA-потока.

    [Fact]
    public void Undo_still_walks_the_rest_of_the_timeline_after_a_paste_was_struck_out()
    {
        var vm = new MainViewModel();
        var paper = (PixelLayer)vm.Document.Layers[0];
        using var img = Patch(SKColors.Red);

        vm.PasteBitmap(img);
        vm.Document.History.ExecuteAndPush(new FillCommand(new SKPointI(500, 500), SKColors.Blue), vm.Document);
        Assert.Equal(SKColors.Blue, paper.Bitmap.GetPixel(500, 500));

        vm.CancelFloatingCommand.Execute(null);
        vm.UndoCommand.Execute(null);

        Assert.Equal(SKColors.White, paper.Bitmap.GetPixel(500, 500));
        Assert.False(vm.Document.History.CanUndo);
    }

    [Fact]
    public void A_paste_that_is_still_the_newest_record_is_undone_the_usual_way()
    {
        var vm = new MainViewModel();
        using var img = Patch(SKColors.Red);
        vm.PasteBitmap(img);

        vm.CancelFloatingCommand.Execute(null);

        // Тот же итог, но другим путём: обычной отменой, которая оставляет запись в
        // хвосте повтора - Ctrl+Y вернёт картинку.
        Assert.Null(vm.Document.FloatingPickup);
        Assert.True(vm.Document.History.CanRedo);
    }

    // ───────── повтор ленты через прижатие ─────────

    [Fact]
    public void Replaying_the_timeline_across_a_committed_paste_leaves_no_second_copy()
    {
        var doc = new Document(60, 60);
        using var img = Patch(SKColors.Red, 20);
        doc.History.ExecuteAndPush(new PasteCommand(img, new SKPoint(5, 5)), doc);
        doc.CommitFloating();
        int target = doc.History.Cursor;
        using var expected = Snap((PixelLayer)doc.Layers[0]);

        doc.History.JumpTo(0, doc);
        doc.History.JumpTo(target, doc);

        // Вставка при повторе кладёт картинку в руки заново, и прижатие обязано её
        // оттуда забрать. Пока запись прижатия только рисовала пиксели, объект оставался
        // висеть поверх них: на экране всё как надо, но стоило потянуть - из-под него
        // выезжала вторая, уже прижатая картинка.
        Assert.Null(doc.FloatingPickup);
        Assert.Equal(0, DiffCount(expected, Snap((PixelLayer)doc.Layers[0])));
    }

    [Fact]
    public void Replaying_the_timeline_never_writes_pixels_no_record_owns()
    {
        var doc = new Document(60, 60);
        using var img = Patch(SKColors.Red, 20);
        doc.History.ExecuteAndPush(new PasteCommand(img, new SKPoint(5, 5)), doc);
        doc.CommitFloating();
        int target = doc.History.Cursor;

        doc.History.JumpTo(0, doc);
        // На нулевой позиции холст обязан быть таким, каким был до вставки.
        Assert.True(((PixelLayer)doc.Layers[0]).IsAllWhite());

        doc.History.JumpTo(target, doc);
        Assert.Equal(target, doc.History.Cursor);
    }

    [Fact]
    public void A_forgotten_command_that_is_not_in_the_timeline_changes_nothing()
    {
        var doc = new Document(20, 20);
        var kept = new FillCommand(new SKPointI(5, 5), SKColors.Red);
        doc.History.ExecuteAndPush(kept, doc);

        Assert.False(doc.History.Forget(new FillCommand(new SKPointI(1, 1), SKColors.Blue)));
        Assert.Equal(new IDocumentCommand[] { kept }, doc.History.Commands);
    }

    // ───────── копия поднятого объекта ─────────

    [Fact]
    public void Copying_a_lifted_object_takes_the_object_without_what_lies_under_it()
    {
        var doc = new Document(60, 60);
        var paper = (PixelLayer)doc.Layers[0];
        FillRect(paper, new SKRect(0, 0, 60, 60), SKColors.White);
        FillRect(paper, new SKRect(10, 10, 20, 20), SKColors.Red);

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 20, 20));
        var fp = doc.FloatingPickup!;
        PickupOps.EnsureLazyErase(doc, fp);
        FillRect(paper, new SKRect(30, 30, 55, 55), SKColors.Blue);
        PickupOps.Translate(fp, 25, 25);
        fp.SetRotation(0.7f);   // углы габарита объектом уже не заняты

        using var copy = ClipboardService.ExtractForClipboard(doc);
        Assert.NotNull(copy);

        // Прежде копия шла через общую сборку, обрезанную по габариту объекта: объект,
        // уведённый на цветное место, попадал в буфер вместе с этим цветом прямоугольной
        // заплаткой, и вставка возвращала на холст не фигуру, а плитку фона с фигурой.
        Assert.Equal(0, copy.GetPixel(0, 0).Alpha);
        Assert.Equal(0, copy.GetPixel(copy.Width - 1, 0).Alpha);
    }

    [Fact]
    public void Copying_a_selection_still_takes_what_the_user_sees()
    {
        var doc = new Document(40, 40);
        doc.Layers.Add(new PixelLayer(40, 40, SKColors.Transparent));
        FillRect((PixelLayer)doc.Layers[1], new SKRect(0, 0, 40, 40), SKColors.Lime);
        doc.Selection = new RectSelection(5, 5, 10, 10);

        using var copy = ClipboardService.ExtractForClipboard(doc);
        Assert.NotNull(copy);

        // У выделения правило прежнее и другое: там копируется сборка всех слоёв.
        Assert.Equal(SKColors.Lime, copy.GetPixel(2, 2));
    }

    // ───────── Shift у ручек ─────────

    [Fact]
    public void Shift_keeps_the_aspect_ratio_while_a_corner_handle_is_dragged()
    {
        var r = GeometryMath.ResizeRotated(0, 0, 100, 50, 0f, ResizeHandle.SE,
            new SKPoint(200, 60), keepAspect: true);
        Assert.Equal(100f / 50f, r.Width / r.Height, 3);
    }

    [Fact]
    public void Shift_leaves_side_handles_alone()
    {
        // Боковая ручка меняет одну сторону по определению - пропорции ей не указ.
        var r = GeometryMath.ResizeRotated(0, 0, 100, 50, 0f, ResizeHandle.E,
            new SKPoint(200, 0), keepAspect: true);
        Assert.Equal(200f, r.Width, 3);
        Assert.Equal(50f, r.Height, 3);
    }

    [Fact]
    public void Shift_keeps_the_ratio_on_a_rotated_object_too()
    {
        var doc = new Document(60, 60);
        PickupOps.PromoteRect(doc, new SKRect(10, 10, 40, 25));
        var fp = doc.FloatingPickup!;
        fp.SetRotation(0.6f);
        float ratio = fp.Width / fp.Height;

        fp.ApplyResize(ResizeHandle.SE, new SKPoint(55, 20), keepAspect: true);

        Assert.Equal(ratio, fp.Width / fp.Height, 2);
    }

    [Fact]
    public void Angle_snapping_rounds_to_the_nearest_fifteen_degrees()
    {
        float step = GeometryMath.RotationSnapStep;
        Assert.Equal(0f, GeometryMath.SnapAngle(0.05f, step), 4);
        Assert.Equal(step, GeometryMath.SnapAngle(0.30f, step), 4);
        Assert.Equal(-step, GeometryMath.SnapAngle(-0.30f, step), 4);
        Assert.Equal(MathF.PI / 2f, GeometryMath.SnapAngle(MathF.PI / 2f + 0.02f, step), 4);
    }

    // ───────── подписи и подсказки ─────────

    [Fact]
    public void Layer_commands_are_named_in_russian_in_the_history_panel()
    {
        var vm = new MainViewModel();
        vm.AddLayerCommand.Execute(null);
        vm.RemoveLayerCommand.Execute(vm.LayerItems[1]);

        var labels = vm.HistoryItems.Select(h => h.Label).ToArray();
        Assert.Contains("Добавление слоя", labels);
        Assert.Contains("Удаление слоя", labels);
    }

    [Fact]
    public void A_stroke_with_the_opacity_at_zero_explains_itself()
    {
        string hint = "";
        var doc = new Document(40, 40);
        var ctx = new ToolContext(doc)
        {
            PrimaryColor = SKColors.Black, ToolSize = 6f, Opacity = 0f, ReportHint = s => hint = s,
        };
        var brush = new BrushTool();
        brush.OnPointerDown(new SKPoint(10, 10), ctx);
        brush.OnPointerUp(new SKPoint(12, 12), ctx);

        // Штрих с нулевой прозрачностью не меняет ни одного пикселя и в историю не идёт
        // (это правило с 1.15.0), но пользователю об этом не говорилось ни слова.
        Assert.NotEqual("", hint);
        Assert.Empty(doc.History.Commands);
    }

    [Fact]
    public void A_shape_with_the_opacity_at_zero_explains_itself()
    {
        string hint = "";
        var doc = new Document(40, 40);
        var ctx = new ToolContext(doc)
        {
            PrimaryColor = SKColors.Black, ToolSize = 4f, Opacity = 0f, ReportHint = s => hint = s,
        };
        var rect = new RectShapeTool();
        rect.OnPointerDown(new SKPoint(5, 5), ctx);
        rect.OnPointerMove(new SKPoint(25, 25), ctx);
        rect.OnPointerUp(new SKPoint(25, 25), ctx);

        Assert.NotEqual("", hint);
        Assert.Empty(doc.History.Commands);
    }

    [Fact]
    public void The_eraser_is_not_told_off_for_a_zero_opacity()
    {
        // Ластик прозрачность не учитывает вовсе - так же, как в Electron-версии.
        string hint = "";
        var doc = new Document(40, 40);
        var ctx = new ToolContext(doc) { ToolSize = 10f, Opacity = 0f, ReportHint = s => hint = s };
        var eraser = new EraserTool();
        eraser.OnPointerDown(new SKPoint(20, 20), ctx);
        eraser.OnPointerUp(new SKPoint(20, 20), ctx);

        Assert.Equal("", hint);
        Assert.Single(doc.History.Commands);
    }

    /// <summary>
    /// Вставленную картинку унесли за край холста и прижали. Прижимать нечего: ни один
    /// пиксель не меняется, и записи о прижатии в ленте не появляется. Запись «Вставка»
    /// при этом обязана уйти вместе с картинкой - иначе история утверждает, что вставка
    /// применена, на холсте её нет, а клик по строке ленты создаёт её заново, из
    /// ниоткуда. Нашёл это фаззинг ленты, сценарным тестом такое не сочинялось.
    /// </summary>
    [Fact]
    public void A_paste_carried_off_the_canvas_leaves_no_record_behind()
    {
        var doc = new Document(40, 40);
        using var img = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(img)) c.Clear(SKColors.Red);

        doc.History.ExecuteAndPush(new PasteCommand(img, new SKPoint(-200, -200)), doc);
        Assert.Equal(1, doc.History.Cursor);

        PickupOps.Translate(doc.FloatingPickup!, -50, -50);
        doc.CommitFloating();

        Assert.Null(doc.FloatingPickup);
        Assert.Empty(doc.History.Commands);
        Assert.False(doc.History.IsDirtySinceSave);
    }

    /// <summary>
    /// Прижатая вставка - ОДНА запись в ленте, а не две.
    ///
    /// Раньше их оставалось две: «Вставка», кладущая картинку в руки, и диф прижатия под
    /// той же подписью. Позиция курсора при этом перестала однозначно задавать документ:
    /// на курсоре 1, куда пришли отменой прижатия, на холсте не было ничего, а на том же
    /// курсоре 1, куда пришли кликом по строке ленты, картинка появлялась в руках - и не
    /// там, куда её перетащили, а в точке вставки. Первый Ctrl+Z убирал картинку с
    /// холста, второй не менял ничего, и вернуть её мог только двойной Ctrl+Y.
    ///
    /// Всё, что вставка означает для холста, описывает диф прижатия, и подпись «Вставка»
    /// несёт он же. Сама запись вставки не трогает ни одного пикселя, поэтому вычёркивается
    /// целиком - тем же способом, каким от неё отказываются Escape и Delete.
    /// </summary>
    [Fact]
    public void A_committed_paste_is_a_single_timeline_entry()
    {
        var doc = new Document(40, 40);
        using var img = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(img)) c.Clear(SKColors.Red);

        doc.History.ExecuteAndPush(new PasteCommand(img, new SKPoint(5, 5)), doc);
        PickupOps.Translate(doc.FloatingPickup!, 10, 10);
        doc.CommitFloating();

        Assert.Single(doc.History.Commands);
        Assert.Equal("Вставка", doc.History.Commands[0].DisplayName);
        Assert.Equal(SKColors.Red, ((PixelLayer)doc.Layers[0]).Bitmap.GetPixel(20, 20));

        // И один Ctrl+Z убирает её целиком.
        doc.History.Undo(doc);
        Assert.Null(doc.FloatingPickup);
        Assert.Equal(SKColors.White, ((PixelLayer)doc.Layers[0]).Bitmap.GetPixel(20, 20));
        Assert.Equal(0, doc.History.Cursor);
    }

    /// <summary>
    /// Та же вставка, но между ней и прижатием легла чужая правка: вычеркнуть надо именно
    /// запись вставки, а не «последнюю».
    /// </summary>
    [Fact]
    public void A_committed_paste_is_folded_even_after_another_edit()
    {
        var doc = new Document(40, 40);
        using var img = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(img)) c.Clear(SKColors.Red);

        doc.History.ExecuteAndPush(new PasteCommand(img, new SKPoint(5, 5)), doc);
        doc.History.ExecuteAndPush(
            new Commands.LayerPropertyCommand(doc.Layers[0], true, 0.5f), doc);
        PickupOps.Translate(doc.FloatingPickup!, 10, 10);
        doc.CommitFloating();

        Assert.Equal(2, doc.History.Commands.Count);
        Assert.Equal("Layer properties", doc.History.Commands[0].DisplayName);
        Assert.Equal("Вставка", doc.History.Commands[1].DisplayName);
        Assert.Equal(2, doc.History.Cursor);
    }
}
