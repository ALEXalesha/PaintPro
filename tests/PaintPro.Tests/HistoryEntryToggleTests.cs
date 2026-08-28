using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Выключатель правки в ленте: убрать одну правку из середины, не трогая те, что идут
/// после неё. Это не то же самое, что щелчок по строке, — тот откатывает всё после
/// выбранной позиции.
///
/// Выключать можно не всё. Штрих, заливка и стирание СКЛАДЫВАЮТСЯ с тем, что на слое, и
/// пропустить любого из них при проигрывании ленты можно. А прижатие объекта, поворот,
/// кадрирование, смена размера и сборка слоёв пишут абсолютный снимок: при пересборке они
/// положат его поверх вместе с тем, что выключили, и выключатель начал бы врать. Поэтому
/// галочка есть у складывающейся правки и только пока ниже неё нет ни одной пишущей снимок.
/// </summary>
public class HistoryEntryToggleTests
{
    private static SKBitmap Snap(Document doc)
        => ((PixelLayer)doc.Layers[0]).ExtractRegion(new SKRectI(0, 0, doc.CanvasWidth, doc.CanvasHeight));

    private static bool Same(SKBitmap a, SKBitmap b)
        => a.Width == b.Width && a.Height == b.Height
           && a.GetPixelSpan().SequenceEqual(b.GetPixelSpan());

    /// <summary>Штрих в своём углу холста, чтобы штрихи друг друга не задевали.</summary>
    private static void Stroke(Document doc, SKColor color, float x, float y)
    {
        var ctx = new ToolContext(doc) { PrimaryColor = color, ToolSize = 6f, Opacity = 1f };
        var t = new BrushTool();
        t.OnPointerDown(new SKPoint(x, y), ctx);
        t.OnPointerMove(new SKPoint(x + 10, y + 10), ctx);
        t.OnPointerUp(new SKPoint(x + 10, y + 10), ctx);
    }

    private static bool IsColor(Document doc, int x, int y, SKColor c)
    {
        var p = ((PixelLayer)doc.Layers[0]).Bitmap.GetPixel(x, y);
        return Math.Abs(p.Red - c.Red) < 40 && Math.Abs(p.Green - c.Green) < 40
            && Math.Abs(p.Blue - c.Blue) < 40;
    }

    /// <summary>Три штриха по разным углам: их можно выключать по одному и проверять глазами теста.</summary>
    private static Document ThreeStrokes()
    {
        var doc = new Document(120, 120);
        Stroke(doc, SKColors.Red, 10, 10);
        Stroke(doc, SKColors.Lime, 50, 50);
        Stroke(doc, SKColors.Blue, 90, 90);
        return doc;
    }

    // ───────── что выключатель делает ─────────

    [Fact]
    public void Disabling_an_entry_removes_only_that_edit()
    {
        var doc = ThreeStrokes();
        var middle = doc.History.Commands[1];

        Assert.True(doc.History.SetEnabled(middle, false, doc));

        Assert.True(IsColor(doc, 13, 13, SKColors.Red), "первый штрих обязан остаться");
        Assert.True(IsColor(doc, 53, 53, SKColors.White), "выключенный штрих обязан пропасть");
        Assert.True(IsColor(doc, 93, 93, SKColors.Blue), "третий штрих обязан остаться");
    }

    [Fact]
    public void Re_enabling_brings_the_document_back_to_the_pixel()
    {
        var doc = ThreeStrokes();
        using var before = Snap(doc);
        var middle = doc.History.Commands[1];

        doc.History.SetEnabled(middle, false, doc);
        doc.History.SetEnabled(middle, true, doc);

        using var after = Snap(doc);
        Assert.True(Same(before, after));
    }

    /// <summary>
    /// Выключить последнюю запись — то же самое, что отменить её обычным Ctrl+Z. Если эти
    /// два пути расходятся, значит расходятся и сами правила ленты.
    /// </summary>
    [Fact]
    public void Disabling_the_last_entry_equals_a_plain_undo()
    {
        var undone = ThreeStrokes();
        undone.History.Undo(undone);
        using var expected = Snap(undone);

        var toggled = ThreeStrokes();
        toggled.History.SetEnabled(toggled.History.Commands[2], false, toggled);
        using var actual = Snap(toggled);

        Assert.True(Same(expected, actual));
    }

    [Fact]
    public void The_cursor_does_not_move_when_an_entry_is_switched_off()
    {
        var doc = ThreeStrokes();
        int before = doc.History.Cursor;

        doc.History.SetEnabled(doc.History.Commands[1], false, doc);

        Assert.Equal(before, doc.History.Cursor);
        Assert.Equal(3, doc.History.Commands.Count);
    }

    /// <summary>
    /// Ctrl+Z после переключения обязан отменять по СВЕЖЕМУ снимку «до». Снимок, снятый
    /// при первой записи, помнит слой вместе с выключенной правкой, и отмена по нему
    /// воскресила бы её.
    /// </summary>
    [Fact]
    public void Undo_after_a_toggle_does_not_resurrect_the_disabled_edit()
    {
        var doc = ThreeStrokes();
        doc.History.SetEnabled(doc.History.Commands[0], false, doc);

        doc.History.Undo(doc);   // снимаем третий штрих

        Assert.True(IsColor(doc, 13, 13, SKColors.White), "выключенный штрих не имеет права вернуться");
        Assert.True(IsColor(doc, 53, 53, SKColors.Lime), "второй штрих обязан остаться");
        Assert.True(IsColor(doc, 93, 93, SKColors.White), "третий штрих обязан быть отменён");
    }

    /// <summary>Прогулка по ленте выключенную правку тоже пропускает.</summary>
    [Fact]
    public void Walking_the_timeline_keeps_the_entry_switched_off()
    {
        var doc = ThreeStrokes();
        doc.History.SetEnabled(doc.History.Commands[1], false, doc);

        doc.History.JumpTo(0, doc);
        doc.History.JumpTo(3, doc);

        Assert.True(IsColor(doc, 13, 13, SKColors.Red));
        Assert.True(IsColor(doc, 53, 53, SKColors.White));
        Assert.True(IsColor(doc, 93, 93, SKColors.Blue));
    }

    // ───────── кому выключатель не положен ─────────

    [Fact]
    public void A_stroke_with_nothing_below_it_can_be_switched_off()
    {
        var doc = ThreeStrokes();
        Assert.All(doc.History.Commands, c => Assert.True(doc.History.CanToggle(c)));
    }

    [Fact]
    public void An_entry_that_writes_a_snapshot_never_gets_a_switch()
    {
        var doc = new Document(60, 60);
        Stroke(doc, SKColors.Red, 10, 10);
        doc.History.ExecuteAndPush(DocumentTransform.Rotate(doc, MathF.PI / 2f), doc);

        var rotate = doc.History.Commands[^1];
        Assert.False(doc.History.CanToggle(rotate));
    }

    [Fact]
    public void A_canvas_edit_below_takes_the_switch_away()
    {
        var doc = new Document(60, 40);
        Stroke(doc, SKColors.Red, 10, 10);
        var stroke = doc.History.Commands[0];
        Assert.True(doc.History.CanToggle(stroke));

        doc.History.ExecuteAndPush(DocumentTransform.Rotate(doc, MathF.PI / 2f), doc);

        Assert.False(doc.History.CanToggle(stroke));
    }

    /// <summary>
    /// Прижатие поднятого объекта в этом списке наравне с поворотом: оно кладёт готовый
    /// снимок области и при пересборке вернуло бы выключенное обратно.
    /// </summary>
    [Fact]
    public void A_committed_pickup_below_takes_the_switch_away()
    {
        var doc = new Document(120, 120);
        Stroke(doc, SKColors.Red, 10, 10);
        var stroke = doc.History.Commands[0];
        Assert.True(doc.History.CanToggle(stroke));

        // Поднимать надо НЕ пустое место: прижатие, не изменившее ни пикселя, в ленту не
        // попадает (правило 1.20.0), и записи, ради которой тест написан, не появилось бы.
        Stroke(doc, SKColors.Lime, 70, 70);
        PickupOps.PromoteRect(doc, new SKRect(60, 60, 100, 100));
        PickupOps.EnsureLazyErase(doc, doc.FloatingPickup!);
        PickupOps.Translate(doc.FloatingPickup!, 5, 5);
        doc.CommitFloating();

        Assert.True(doc.History.Commands.Count >= 3, "прижатие обязано было записаться");
        Assert.False(doc.History.CanToggle(stroke));
    }

    [Fact]
    public void Switching_off_a_forbidden_entry_refuses_and_changes_nothing()
    {
        var doc = new Document(60, 40);
        Stroke(doc, SKColors.Red, 10, 10);
        var stroke = doc.History.Commands[0];
        doc.History.ExecuteAndPush(DocumentTransform.Rotate(doc, MathF.PI / 2f), doc);
        using var before = Snap(doc);

        Assert.False(doc.History.SetEnabled(stroke, false, doc));

        using var after = Snap(doc);
        Assert.True(Same(before, after));
        Assert.True(doc.History.IsEnabled(stroke));
    }

    // ───────── признак несохранённой работы ─────────

    [Fact]
    public void Switching_an_entry_off_marks_the_document_dirty()
    {
        var doc = ThreeStrokes();
        doc.History.MarkSaved();
        Assert.False(doc.History.IsDirtySinceSave);

        doc.History.SetEnabled(doc.History.Commands[1], false, doc);

        Assert.True(doc.History.IsDirtySinceSave);
    }

    [Fact]
    public void Switching_it_back_on_makes_the_document_clean_again()
    {
        var doc = ThreeStrokes();
        doc.History.MarkSaved();

        doc.History.SetEnabled(doc.History.Commands[1], false, doc);
        doc.History.SetEnabled(doc.History.Commands[1], true, doc);

        Assert.False(doc.History.IsDirtySinceSave);
    }

    [Fact]
    public void Saving_with_an_entry_switched_off_makes_that_the_new_clean_state()
    {
        var doc = ThreeStrokes();
        doc.History.SetEnabled(doc.History.Commands[1], false, doc);
        doc.History.MarkSaved();

        Assert.False(doc.History.IsDirtySinceSave);

        doc.History.SetEnabled(doc.History.Commands[1], true, doc);
        Assert.True(doc.History.IsDirtySinceSave);
    }

    // ───────── панель ─────────

    [Fact]
    public void The_panel_shows_a_switch_only_where_it_is_allowed()
    {
        var vm = new MainViewModel();
        Stroke(vm.Document, SKColors.Red, 20, 20);
        vm.Document.History.ExecuteAndPush(DocumentTransform.Flip(vm.Document, true), vm.Document);

        // строка 0 - «Исходное состояние», у неё записи нет вовсе
        Assert.False(vm.HistoryItems[0].CanToggle);
        Assert.False(vm.HistoryItems[1].CanToggle);   // штрих: ниже отражение
        Assert.False(vm.HistoryItems[2].CanToggle);   // само отражение
        Assert.All(vm.HistoryItems, e => Assert.NotNull(e.ToggleHint));
    }

    [Fact]
    public void The_panel_toggles_through_the_view_model()
    {
        var vm = new MainViewModel();
        Stroke(vm.Document, SKColors.Red, 20, 20);
        Stroke(vm.Document, SKColors.Blue, 60, 60);

        var row = vm.HistoryItems[1];
        Assert.True(row.CanToggle);
        Assert.True(row.Enabled);

        vm.ToggleHistoryEntryCommand.Execute(row);

        Assert.False(vm.HistoryItems[1].Enabled);
        Assert.True(IsColor(vm.Document, 23, 23, SKColors.White));
        Assert.True(IsColor(vm.Document, 63, 63, SKColors.Blue));
    }

    [Fact]
    public void The_panel_explains_a_refused_toggle()
    {
        var vm = new MainViewModel();
        Stroke(vm.Document, SKColors.Red, 20, 20);
        var row = vm.HistoryItems[1];
        vm.Document.History.ExecuteAndPush(DocumentTransform.Flip(vm.Document, true), vm.Document);

        vm.ToggleHistoryEntryCommand.Execute(row);

        Assert.NotEqual("", vm.StatusHint);
    }
}
