using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Дописанная в последнюю запись правка против признака несохранённой работы,
/// рисование по скрытому слою и поднятое, но не сдвинутое выделение.
/// </summary>
public class HiddenLayerAndAmendTests
{
    private sealed class Noop : IDocumentCommand
    {
        public string DisplayName => "Пусто";
        public void Execute(Document doc) { }
        public void Undo(Document doc) { }
    }

    private sealed class Tracked : IDocumentCommand, IDisposable
    {
        public bool Disposed { get; private set; }
        public string DisplayName => "Слежка";
        public void Execute(Document doc) { }
        public void Undo(Document doc) { }
        public void Dispose() => Disposed = true;
    }

    // ───────── склейка правок и метка сохранения ─────────

    [Fact]
    public void Amending_the_saved_entry_makes_the_document_dirty()
    {
        var doc = new Document(8, 8);
        var h = doc.History;
        h.Push(new Noop());
        h.MarkSaved();                  // в файле — состояние на позиции 1
        Assert.False(h.IsDirtySinceSave);

        h.AmendCurrent();               // MergeInto дописал в ту же запись

        Assert.Equal(1, h.Cursor);
        Assert.True(h.IsDirtySinceSave);
    }

    [Fact]
    public void Amending_a_later_entry_leaves_an_older_saved_mark_alone()
    {
        var doc = new Document(8, 8);
        var h = doc.History;
        h.Push(new Noop());
        h.MarkSaved();                  // сохранено на позиции 1
        h.Push(new Noop());             // позиция 2, документ уже изменён

        h.AmendCurrent();               // склейка тронула запись на позиции 2

        // Метка на позиции 1 всё ещё достижима: до неё склейка не дотянулась.
        h.Undo(doc);
        Assert.False(h.IsDirtySinceSave);
    }

    [Fact]
    public void Amending_drops_the_redo_tail_and_frees_it()
    {
        var doc = new Document(8, 8);
        var h = doc.History;
        h.Push(new Noop());
        var tail = new Tracked();
        h.Push(tail);
        h.Undo(doc);                    // курсор 1, в хвосте лежит tail

        h.AmendCurrent();

        Assert.Equal(0, h.RedoDepth);
        Assert.True(tail.Disposed);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Amending_an_empty_history_does_nothing()
    {
        var doc = new Document(8, 8);
        var h = doc.History;
        h.MarkSaved();

        h.AmendCurrent();

        Assert.False(h.IsDirtySinceSave);
    }

    // ───────── скрытый слой ─────────

    /// <summary>Слой пуст. Сравниваем с нулевым цветом, а не с SKColors.Transparent:
    /// тот - прозрачный БЕЛЫЙ (255,255,255,0), и premul-битмап ему не равен.</summary>
    private static bool IsBlank(PixelLayer layer) => layer.IsAllColor(new SKColor(0, 0, 0, 0));

    private static (Document doc, ToolContext ctx, PixelLayer layer) HiddenTopLayer()
    {
        var doc = new Document(32, 32);
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Верхний"), doc);
        var layer = (PixelLayer)doc.Layers[1];
        layer.Visible = false;
        doc.ActiveLayerIndex = 1;
        return (doc, new ToolContext(doc) { PrimaryColor = SKColors.Black, ToolSize = 8f, Opacity = 1f }, layer);
    }

    [Fact]
    public void Stroke_on_a_hidden_layer_changes_nothing_and_says_why()
    {
        var (doc, ctx, layer) = HiddenTopLayer();
        string? hint = null;
        ctx.ReportHint = h => hint = h;
        int before = doc.History.UndoDepth;

        var brush = new BrushTool();
        brush.OnPointerDown(new SKPoint(16, 16), ctx);
        brush.OnPointerMove(new SKPoint(20, 20), ctx);
        brush.OnPointerUp(new SKPoint(20, 20), ctx);

        Assert.Equal(before, doc.History.UndoDepth);
        Assert.True(IsBlank(layer));
        Assert.False(ctx.IsDrawing);
        Assert.Contains("скрыт", hint);
    }

    [Fact]
    public void Fill_on_a_hidden_layer_changes_nothing()
    {
        var (doc, ctx, layer) = HiddenTopLayer();
        int before = doc.History.UndoDepth;

        new FillTool().OnPointerDown(new SKPoint(16, 16), ctx);

        Assert.Equal(before, doc.History.UndoDepth);
        Assert.True(IsBlank(layer));
    }

    [Fact]
    public void Shape_on_a_hidden_layer_changes_nothing()
    {
        var (doc, ctx, layer) = HiddenTopLayer();
        int before = doc.History.UndoDepth;

        var rect = new RectShapeTool();
        rect.OnPointerDown(new SKPoint(4, 4), ctx);
        rect.OnPointerMove(new SKPoint(24, 24), ctx);
        rect.OnPointerUp(new SKPoint(24, 24), ctx);

        Assert.Equal(before, doc.History.UndoDepth);
        Assert.True(IsBlank(layer));
        Assert.Equal(DocumentMode.Idle, doc.Mode);
    }

    [Fact]
    public void Visible_layer_still_takes_the_stroke()
    {
        var (doc, ctx, layer) = HiddenTopLayer();
        layer.Visible = true;
        int before = doc.History.UndoDepth;

        var brush = new BrushTool();
        brush.OnPointerDown(new SKPoint(16, 16), ctx);
        brush.OnPointerUp(new SKPoint(16, 16), ctx);

        Assert.Equal(before + 1, doc.History.UndoDepth);
        Assert.False(IsBlank(layer));
    }

    // ───────── подъём без перемещения ─────────

    [Fact]
    public void Lifting_a_selection_without_moving_it_is_not_an_edit()
    {
        var doc = new Document(32, 32);
        PickupOps.PromoteRect(doc, new SKRect(4, 4, 20, 20));

        Assert.NotNull(doc.FloatingPickup);
        // Именно по этому флагу вьюмодель решает, есть ли несохранённая работа:
        // до первого сдвига холст не тронут, и спрашивать про сохранение не о чем.
        Assert.False(doc.FloatingPickup!.OriginalAreaErased);
        Assert.False(doc.History.IsDirtySinceSave);
    }

    [Fact]
    public void Moving_the_pickup_marks_the_document_changed()
    {
        var doc = new Document(32, 32);
        PickupOps.PromoteRect(doc, new SKRect(4, 4, 20, 20));
        var fp = doc.FloatingPickup!;

        PickupOps.EnsureLazyErase(doc, fp);
        fp.X += 6;

        Assert.True(fp.OriginalAreaErased);
    }
}
