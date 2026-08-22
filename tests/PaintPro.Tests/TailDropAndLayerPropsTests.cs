using System.IO;
using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Метка сохранения при срезанном хвосте повтора, поднятое выделение против отмены,
/// свойства слоя в истории, освобождение вытесненных записей и отказ при открытии файла.
/// </summary>
public class TailDropAndLayerPropsTests
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

    // ───────── метка сохранения ─────────

    [Fact]
    public void Edit_after_undo_landing_on_the_saved_index_is_still_dirty()
    {
        var doc = new Document(8, 8);
        var h = doc.History;
        h.Push(new Noop());
        h.Push(new Noop());
        h.MarkSaved();          // в файле — состояние на позиции 2

        h.Undo(doc);
        h.Push(new Noop());     // хвост срезан, курсор снова 2, но правка другая

        Assert.Equal(2, h.Cursor);
        Assert.True(h.IsDirtySinceSave);
    }

    [Fact]
    public void Redo_back_to_the_saved_state_still_counts_as_clean()
    {
        var doc = new Document(8, 8);
        var h = doc.History;
        h.Push(new Noop());
        h.MarkSaved();
        h.Push(new Noop());

        h.Undo(doc);
        h.Redo(doc);
        Assert.True(h.IsDirtySinceSave);

        h.Undo(doc);
        Assert.False(h.IsDirtySinceSave);
    }

    // ───────── поднятое выделение и откат ─────────

    [Fact]
    public void Undo_returns_the_lifted_pixels_before_touching_the_timeline()
    {
        var doc = new Document(20, 20);
        var layer = (PixelLayer)doc.Layers[0];
        using (var c = new SKCanvas(layer.Bitmap))
        using (var p = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill })
            c.DrawRect(new SKRect(2, 2, 8, 8), p);

        doc.History.Push(new Noop());
        PickupOps.PromoteRect(doc, new SKRect(2, 2, 8, 8));
        PickupOps.EnsureLazyErase(doc, doc.FloatingPickup!);
        doc.FloatingPickup!.X += 6;

        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(4, 4)); // дыра на месте подъёма

        doc.History.Undo(doc);

        Assert.Null(doc.FloatingPickup);
        Assert.Equal(1, doc.History.Cursor);                       // сама история не сдвинулась
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(4, 4));   // пиксели вернулись
    }

    [Fact]
    public void Jump_through_history_drops_the_pickup()
    {
        var doc = new Document(20, 20);
        doc.History.Push(new Noop());
        doc.History.Push(new Noop());
        PickupOps.PromoteRect(doc, new SKRect(2, 2, 8, 8));
        Assert.NotNull(doc.FloatingPickup);

        doc.History.JumpTo(0, doc);

        Assert.Null(doc.FloatingPickup);
        Assert.Equal(0, doc.History.Cursor);
    }

    // ───────── свойства слоя ─────────

    [Fact]
    public void Layer_visibility_and_opacity_go_through_history()
    {
        var doc = new Document(8, 8);
        var layer = doc.Layers[0];

        doc.History.ExecuteAndPush(new LayerPropertyCommand(layer, false, 0.5f), doc);
        Assert.False(layer.Visible);
        Assert.Equal(0.5f, layer.Opacity, 3);
        Assert.True(doc.History.IsDirtySinceSave);

        doc.History.Undo(doc);
        Assert.True(layer.Visible);
        Assert.Equal(1f, layer.Opacity, 3);

        doc.History.Redo(doc);
        Assert.False(layer.Visible);
    }

    [Fact]
    public void Consecutive_slider_steps_stay_one_history_entry()
    {
        var doc = new Document(8, 8);
        var layer = doc.Layers[0];

        var cmd = new LayerPropertyCommand(layer, true, 0.8f);
        doc.History.ExecuteAndPush(cmd, doc);
        cmd.MergeInto(doc, true, 0.6f);
        cmd.MergeInto(doc, true, 0.2f);

        Assert.Single(doc.History.Commands);
        Assert.Equal(0.2f, layer.Opacity, 3);

        doc.History.Undo(doc);
        Assert.Equal(1f, layer.Opacity, 3);
    }

    [Fact]
    public void A_property_edit_that_changes_nothing_is_not_recorded()
    {
        var doc = new Document(8, 8);
        var cmd = new LayerPropertyCommand(doc.Layers[0], true, 1f);
        Assert.False(cmd.ChangedAnything);
    }

    // ───────── освобождение вытесненных записей ─────────

    [Fact]
    public void Dropping_by_depth_releases_the_command()
    {
        var doc = new Document(8, 8);
        var h = doc.History;
        h.MaxDepth = 2;
        var oldest = new Tracked();
        h.Push(oldest);
        h.Push(new Noop());
        Assert.False(oldest.Disposed);

        h.Push(new Noop());
        Assert.True(oldest.Disposed);
    }

    [Fact]
    public void Cutting_the_redo_tail_releases_it()
    {
        var doc = new Document(8, 8);
        var h = doc.History;
        h.Push(new Noop());
        var future = new Tracked();
        h.Push(future);

        h.Undo(doc);
        h.Push(new Noop());

        Assert.True(future.Disposed);
    }

    // ───────── открытие файла ─────────

    [Fact]
    public void Opening_something_that_is_not_an_image_reports_a_failure()
    {
        var path = Path.Combine(Path.GetTempPath(), "paint-pro-not-an-image.png");
        File.WriteAllText(path, "мусор, а не картинка");
        try
        {
            var outcome = new FileService().OpenImage(path);
            Assert.Equal(OpenStatus.Failed, outcome.Status);
            Assert.Null(outcome.Bitmap);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_failed_open_does_not_become_the_target_of_the_next_save()
    {
        var files = new FileService();
        files.OpenImage(Path.Combine(Path.GetTempPath(), "paint-pro-no-such-file.png"));
        Assert.Null(files.LastOpenedPath);
        Assert.Null(files.LastSavedPath);
    }
}
