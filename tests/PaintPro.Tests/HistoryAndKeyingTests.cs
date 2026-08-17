using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Covers the list+cursor HistoryManager (undo/redo/jump/trim), the new undoable
/// EraseRegionCommand, and the white-background keying used when lifting a selection.
/// </summary>
public class HistoryAndKeyingTests
{
    /// <summary>A command that appends a tag to a shared log on Execute/Undo — lets us assert ordering.</summary>
    private sealed class LogCommand : IDocumentCommand
    {
        private readonly List<string> _log;
        private readonly string _tag;
        public LogCommand(List<string> log, string tag) { _log = log; _tag = tag; }
        public string DisplayName => _tag;
        public void Execute(Document doc) => _log.Add("+" + _tag);
        public void Undo(Document doc) => _log.Add("-" + _tag);
    }

    [Fact]
    public void Push_undo_redo_track_cursor()
    {
        var doc = new Document(10, 10);
        var h = doc.History;
        var log = new List<string>();

        h.ExecuteAndPush(new LogCommand(log, "A"), doc);
        h.ExecuteAndPush(new LogCommand(log, "B"), doc);
        Assert.Equal(2, h.Cursor);
        Assert.True(h.CanUndo);
        Assert.False(h.CanRedo);

        Assert.True(h.Undo(doc));
        Assert.Equal(1, h.Cursor);
        Assert.True(h.CanRedo);

        Assert.True(h.Redo(doc));
        Assert.Equal(2, h.Cursor);

        Assert.Equal(new[] { "+A", "+B", "-B", "+B" }, log);
    }

    [Fact]
    public void Push_after_undo_drops_the_redo_tail()
    {
        var doc = new Document(10, 10);
        var h = doc.History;
        var log = new List<string>();

        h.ExecuteAndPush(new LogCommand(log, "A"), doc);
        h.ExecuteAndPush(new LogCommand(log, "B"), doc);
        h.Undo(doc);                                   // back to after A
        h.ExecuteAndPush(new LogCommand(log, "C"), doc); // B is discarded

        Assert.Equal(2, h.Commands.Count);
        Assert.Equal("A", h.Commands[0].DisplayName);
        Assert.Equal("C", h.Commands[1].DisplayName);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void JumpTo_replays_to_an_arbitrary_position()
    {
        var doc = new Document(10, 10);
        var h = doc.History;
        var log = new List<string>();
        h.ExecuteAndPush(new LogCommand(log, "A"), doc);
        h.ExecuteAndPush(new LogCommand(log, "B"), doc);
        h.ExecuteAndPush(new LogCommand(log, "C"), doc);
        log.Clear();

        h.JumpTo(0, doc); // undo all, newest first
        Assert.Equal(0, h.Cursor);
        Assert.Equal(new[] { "-C", "-B", "-A" }, log);

        log.Clear();
        h.JumpTo(2, doc); // redo A then B
        Assert.Equal(2, h.Cursor);
        Assert.Equal(new[] { "+A", "+B" }, log);
    }

    [Fact]
    public void Trim_drops_oldest_and_keeps_cursor_consistent()
    {
        var doc = new Document(10, 10);
        var h = doc.History;
        h.MaxDepth = 3;
        var log = new List<string>();
        for (int i = 0; i < 5; i++) h.ExecuteAndPush(new LogCommand(log, i.ToString()), doc);

        Assert.Equal(3, h.Commands.Count);
        Assert.Equal(3, h.Cursor);
        // Oldest two ("0","1") dropped; newest three remain.
        Assert.Equal(new[] { "2", "3", "4" }, h.Commands.Select(c => c.DisplayName).ToArray());
    }

    [Fact]
    public void EraseRegionCommand_is_reversible()
    {
        var doc = new Document(20, 20);
        var pl = (PixelLayer)doc.ActiveLayer;
        using (var c = new SKCanvas(pl.Bitmap))
        using (var p = new SKPaint { Color = SKColors.Red })
            c.DrawRect(new SKRect(5, 5, 15, 15), p);

        Assert.Equal(SKColors.Red, pl.Bitmap.GetPixel(10, 10));

        var cmd = new EraseRegionCommand(new SKRectI(5, 5, 15, 15));
        doc.History.ExecuteAndPush(cmd, doc);
        Assert.Equal(SKColors.White, pl.Bitmap.GetPixel(10, 10));

        doc.History.Undo(doc);
        Assert.Equal(SKColors.Red, pl.Bitmap.GetPixel(10, 10));
    }

    [Fact]
    public void Committing_a_moved_selection_is_undoable()
    {
        var doc = new Document(20, 20);
        var pl = (PixelLayer)doc.ActiveLayer;
        using (var c = new SKCanvas(pl.Bitmap))
        using (var p = new SKPaint { Color = SKColors.Red })
            c.DrawRect(new SKRect(5, 5, 15, 15), p); // red block at 5..15

        // Lift the block: snapshot the layer, key out white, then mimic the tool's lazy-erase.
        var snapshot = pl.ExtractRegion(new SKRectI(0, 0, 20, 20));
        using var raw = pl.ExtractRegion(new SKRectI(5, 5, 15, 15));
        var keyed = BitmapKeying.KeyOutBackground(raw, SKColors.White);
        var pickup = new FloatingPickup(keyed, new SKRect(5, 5, 15, 15))
        {
            PreEditSnapshot = snapshot,
            OriginalAreaErased = true,
        };
        using (var c = new SKCanvas(pl.Bitmap))           // lazy-erase the source area to white
        using (var p = new SKPaint { Color = SKColors.White })
            c.DrawRect(new SKRect(5, 5, 15, 15), p);
        pickup.X = 10; pickup.Y = 10;                      // move +5,+5 -> lands at 10..20
        doc.FloatingPickup = pickup;

        doc.CommitFloating();

        Assert.Equal(1, doc.History.Cursor);               // move recorded
        Assert.Equal(SKColors.White, pl.Bitmap.GetPixel(7, 7));   // source vacated
        Assert.Equal(SKColors.Red, pl.Bitmap.GetPixel(12, 12));   // content at destination

        doc.History.Undo(doc);
        Assert.Equal(SKColors.Red, pl.Bitmap.GetPixel(7, 7));     // original block restored
        Assert.Equal(SKColors.White, pl.Bitmap.GetPixel(17, 17)); // destination cleared
    }

    [Fact]
    public void Committing_a_pickup_without_snapshot_is_still_undoable()
    {
        // Paste-style pickup: nothing was lifted off the layer, so there is no pre-edit
        // snapshot. The merge still has to land in history, otherwise Ctrl+Z after
        // pressing Enter on a paste does nothing.
        var doc = new Document(20, 20);
        var bmp = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Red);
        doc.FloatingPickup = new FloatingPickup(bmp, new SKRect(0, 0, 10, 10))
        {
            OriginalAreaErased = true,
            CommitLabel = "Вставка",
            SourceLayerId = doc.ActiveLayer.Id,
        };

        doc.CommitFloating();

        var layer = (PixelLayer)doc.ActiveLayer;
        Assert.Equal(1, doc.History.Cursor);
        Assert.True(doc.History.CanUndo);
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(5, 5));

        doc.History.Undo(doc);
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(5, 5));
    }

    [Fact]
    public void Cancelling_a_pickup_restores_the_lifted_pixels()
    {
        // Escape must leave no trace: the lazily-erased source area comes back.
        var doc = new Document(20, 20);
        var layer = (PixelLayer)doc.ActiveLayer;
        using (var c = new SKCanvas(layer.Bitmap))
        {
            c.Clear(SKColors.White);
            c.DrawRect(new SKRect(2, 2, 8, 8), new SKPaint { Color = SKColors.Blue });
        }

        var snapshot = layer.ExtractRegion(new SKRectI(0, 0, 20, 20));
        var lifted = layer.ExtractRegion(new SKRectI(2, 2, 8, 8));
        doc.FloatingPickup = new FloatingPickup(lifted, new SKRect(2, 2, 8, 8))
        {
            SourceLayerId = layer.Id,
            PreEditSnapshot = snapshot,
        };
        PickupOps.EnsureLazyErase(doc, doc.FloatingPickup!);
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(5, 5)); // hole is there

        doc.FloatingPickup!.X += 6;
        doc.CancelFloating();

        Assert.Null(doc.FloatingPickup);
        Assert.Equal(SKColors.Blue, layer.Bitmap.GetPixel(5, 5));  // hole filled back in
        Assert.Equal(0, doc.History.Cursor);                       // and nothing recorded
    }

    [Fact]
    public void Commands_write_to_their_own_layer_not_the_active_one()
    {
        var doc = new Document(10, 10);
        var background = (PixelLayer)doc.Layers[0];
        var upper = new PixelLayer(10, 10, SKColors.Transparent);
        doc.Layers.Add(upper);

        // Record a fill against the background layer…
        var fill = new FillCommand(new SKPointI(5, 5), SKColors.Red);
        doc.History.ExecuteAndPush(fill, doc);
        Assert.Equal(SKColors.Red, background.Bitmap.GetPixel(5, 5));

        // …then switch layers and undo. The pixels must come back on the background.
        doc.ActiveLayerIndex = 1;
        doc.History.Undo(doc);

        Assert.Equal(SKColors.White, background.Bitmap.GetPixel(5, 5));
        Assert.Equal((byte)0, upper.Bitmap.GetPixel(5, 5).Alpha);
    }

    [Fact]
    public void History_ignores_pushes_made_while_it_is_applying()
    {
        var doc = new Document(10, 10);
        var h = doc.History;
        var log = new List<string>();
        h.ExecuteAndPush(new LogCommand(log, "A"), doc);
        h.ExecuteAndPush(new ReentrantCommand(h), doc);

        Assert.Equal(2, h.Cursor);
        h.Undo(doc);   // the command tries to Push from inside Undo

        Assert.Equal(2, h.Commands.Count);
        Assert.Equal(1, h.Cursor);
    }

    /// <summary>Stands in for CommitFloating: pushes into history from inside its own Undo.</summary>
    private sealed class ReentrantCommand : IDocumentCommand
    {
        private readonly HistoryManager _history;
        public ReentrantCommand(HistoryManager history) => _history = history;
        public string DisplayName => "Reentrant";
        public void Execute(Document doc) { }
        public void Undo(Document doc) => _history.Push(new ClearCanvasCommand());
    }

    [Fact]
    public void KeyOutBackground_makes_white_transparent_and_keeps_content()
    {
        using var src = new SKBitmap(2, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        src.SetPixel(0, 0, SKColors.White);
        src.SetPixel(1, 0, SKColors.Red);

        using var keyed = BitmapKeying.KeyOutBackground(src, SKColors.White);

        Assert.Equal((byte)0, keyed.GetPixel(0, 0).Alpha);   // white background -> transparent
        Assert.Equal(SKColors.Red, keyed.GetPixel(1, 0));    // drawn content kept
    }
}
