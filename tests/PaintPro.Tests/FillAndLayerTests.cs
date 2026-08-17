using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Tools;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Covers the scanline flood fill, undoable layer add/remove, the eraser's behaviour on
/// upper layers, and the history memory budget.
/// </summary>
public class FillAndLayerTests
{
    private static Document WhiteDoc(int w = 32, int h = 32)
    {
        var doc = new Document(w, h);
        return doc;
    }

    private static void Rect(PixelLayer layer, SKRect r, SKColor color)
    {
        using var c = new SKCanvas(layer.Bitmap);
        using var p = new SKPaint { Color = color, IsAntialias = false };
        c.DrawRect(r, p);
    }

    [Fact]
    public void Fill_spreads_across_the_whole_open_area()
    {
        var doc = WhiteDoc();
        var layer = (PixelLayer)doc.ActiveLayer;

        doc.History.ExecuteAndPush(new FillCommand(new SKPointI(0, 0), SKColors.Red), doc);

        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(31, 31));
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(17, 4));
    }

    [Fact]
    public void Fill_stops_at_a_border_and_leaves_the_other_side_alone()
    {
        var doc = WhiteDoc();
        var layer = (PixelLayer)doc.ActiveLayer;
        // Vertical black wall splitting the canvas in two.
        Rect(layer, new SKRect(16, 0, 17, 32), SKColors.Black);

        doc.History.ExecuteAndPush(new FillCommand(new SKPointI(0, 0), SKColors.Red), doc);

        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(15, 20));    // left side filled
        Assert.Equal(SKColors.Black, layer.Bitmap.GetPixel(16, 20));  // wall intact
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(17, 20));  // right side untouched
    }

    [Fact]
    public void Fill_reaches_areas_only_connected_around_a_corner()
    {
        // A U-shape: the seed can only reach the right arm by going down, across and up.
        // This is what breaks naive scanline seeding.
        var doc = WhiteDoc();
        var layer = (PixelLayer)doc.ActiveLayer;
        Rect(layer, new SKRect(8, 0, 24, 24), SKColors.Black);   // block with an open bottom
        Rect(layer, new SKRect(8, 24, 24, 32), SKColors.White);

        doc.History.ExecuteAndPush(new FillCommand(new SKPointI(0, 0), SKColors.Red), doc);

        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(0, 0));    // left arm
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(16, 28));  // through the gap
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(31, 0));   // and up the right arm
        Assert.Equal(SKColors.Black, layer.Bitmap.GetPixel(16, 12));
    }

    [Fact]
    public void Fill_undo_restores_the_original_pixels()
    {
        var doc = WhiteDoc();
        var layer = (PixelLayer)doc.ActiveLayer;
        Rect(layer, new SKRect(4, 4, 12, 12), SKColors.Blue);

        doc.History.ExecuteAndPush(new FillCommand(new SKPointI(0, 0), SKColors.Red), doc);
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(0, 0));

        doc.History.Undo(doc);

        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(0, 0));
        Assert.Equal(SKColors.Blue, layer.Bitmap.GetPixel(6, 6));
    }

    [Fact]
    public void Fill_with_the_colour_already_there_changes_nothing()
    {
        var doc = WhiteDoc();
        doc.History.ExecuteAndPush(new FillCommand(new SKPointI(5, 5), SKColors.White), doc);
        Assert.True(((PixelLayer)doc.ActiveLayer).IsAllWhite());
    }

    [Fact]
    public void Removing_a_layer_is_undoable_and_brings_the_pixels_back()
    {
        var doc = WhiteDoc();
        var upper = new PixelLayer(32, 32, SKColors.Transparent) { Name = "Слой 1" };
        Rect(upper, new SKRect(4, 4, 12, 12), SKColors.Green);
        doc.Layers.Add(upper);
        var id = upper.Id;

        doc.History.ExecuteAndPush(LayerStackCommand.Remove(doc, upper), doc);
        Assert.Single(doc.Layers);

        doc.History.Undo(doc);

        Assert.Equal(2, doc.Layers.Count);
        var restored = doc.FindPixelLayer(id);
        Assert.NotNull(restored);
        Assert.Equal("Слой 1", restored!.Name);
        Assert.Equal(SKColors.Green, restored.Bitmap.GetPixel(6, 6));
    }

    [Fact]
    public void Adding_a_layer_is_undoable()
    {
        var doc = WhiteDoc();
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Слой 1"), doc);
        Assert.Equal(2, doc.Layers.Count);
        Assert.Equal(1, doc.ActiveLayerIndex);

        doc.History.Undo(doc);
        Assert.Single(doc.Layers);

        doc.History.Redo(doc);
        Assert.Equal(2, doc.Layers.Count);
    }

    [Fact]
    public void Eraser_clears_an_upper_layer_instead_of_painting_it_white()
    {
        var doc = WhiteDoc();
        var upper = new PixelLayer(32, 32, SKColors.Transparent);
        Rect(upper, new SKRect(0, 0, 32, 32), SKColors.Green);
        doc.Layers.Add(upper);
        doc.ActiveLayerIndex = 1;

        var ctx = new ToolContext(doc) { ToolSize = 12f, Opacity = 1f };
        var eraser = new EraserTool();
        eraser.OnPointerDown(new SKPoint(16, 16), ctx);
        eraser.OnPointerUp(new SKPoint(16, 16), ctx);

        // Erased to transparency, not to an opaque white hole punched through the stack.
        Assert.Equal((byte)0, upper.Bitmap.GetPixel(16, 16).Alpha);
        Assert.Equal(SKColors.Green, upper.Bitmap.GetPixel(1, 1));
    }

    [Fact]
    public void Eraser_still_paints_white_on_the_bottom_layer()
    {
        var doc = WhiteDoc();
        var layer = (PixelLayer)doc.ActiveLayer;
        Rect(layer, new SKRect(0, 0, 32, 32), SKColors.Green);

        var ctx = new ToolContext(doc) { ToolSize = 12f, Opacity = 1f };
        var eraser = new EraserTool();
        eraser.OnPointerDown(new SKPoint(16, 16), ctx);
        eraser.OnPointerUp(new SKPoint(16, 16), ctx);

        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(16, 16));
    }

    [Fact]
    public void History_drops_the_oldest_entries_once_the_memory_budget_is_hit()
    {
        var doc = WhiteDoc(64, 64);
        var h = doc.History;
        h.MaxBytes = 40 * 1024; // a few 64×64 snapshots' worth

        for (int i = 0; i < 12; i++)
            h.ExecuteAndPush(new ClearCanvasCommand(
                i % 2 == 0 ? SKColors.Red : SKColors.Blue), doc);

        long total = 0;
        foreach (var c in h.Commands) total += c.ApproximateBytes;

        Assert.True(h.Commands.Count < 12, "старые записи должны были вытесниться");
        Assert.True(total <= h.MaxBytes, $"бюджет превышен: {total} > {h.MaxBytes}");
        Assert.True(h.CanUndo, "последняя правка должна оставаться отменяемой");
    }

    [Fact]
    public void Composite_sampling_sees_through_a_transparent_upper_layer()
    {
        var doc = WhiteDoc();
        Rect((PixelLayer)doc.Layers[0], new SKRect(0, 0, 32, 32), SKColors.Blue);
        var upper = new PixelLayer(32, 32, SKColors.Transparent);
        Rect(upper, new SKRect(0, 0, 8, 8), SKColors.Red);
        doc.Layers.Add(upper);

        Assert.Equal(SKColors.Red, doc.SampleComposite(4, 4));    // covered by the upper layer
        Assert.Equal(SKColors.Blue, doc.SampleComposite(20, 20)); // upper layer is empty here
    }
}
