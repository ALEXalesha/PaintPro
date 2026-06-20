using PaintPro.Models;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Tests covering the document state-machine invariants. These map directly to
/// antipatterns #1, #4 and #7 from REWRITE_PROMPT_CSHARP.md — each test exists
/// because we hit that bug in the Electron version.
/// </summary>
public class DocumentInvariantTests
{
    private static FloatingPickup MakeDummyPickup()
    {
        var bmp = new SKBitmap(10, 10);
        return new FloatingPickup(bmp, new SKRect(0, 0, 10, 10));
    }

    // —— Antipattern #4: Selection and FloatingPickup are mutually exclusive ——

    [Fact]
    public void Selection_and_floating_are_mutually_exclusive_setting_floating_clears_selection()
    {
        var doc = new Document(900, 600);
        doc.Selection = new RectSelection(0, 0, 100, 100);
        Assert.NotNull(doc.Selection);

        doc.FloatingPickup = MakeDummyPickup();

        Assert.Null(doc.Selection);
        Assert.NotNull(doc.FloatingPickup);
    }

    [Fact]
    public void Setting_selection_while_floating_active_commits_floating()
    {
        var doc = new Document(900, 600);
        doc.FloatingPickup = MakeDummyPickup();
        Assert.NotNull(doc.FloatingPickup);

        doc.Selection = new RectSelection(10, 10, 50, 50);

        Assert.Null(doc.FloatingPickup);    // committed into the active layer
        Assert.NotNull(doc.Selection);      // new selection in place
    }

    // —— Mode derivation ——

    [Fact]
    public void Mode_is_Idle_when_nothing_selected()
    {
        var doc = new Document(900, 600);
        Assert.Equal(DocumentMode.Idle, doc.Mode);
    }

    [Fact]
    public void Mode_is_SelectionRect_when_rect_selection_present()
    {
        var doc = new Document(900, 600);
        doc.Selection = new RectSelection(0, 0, 100, 100);
        Assert.Equal(DocumentMode.SelectionRect, doc.Mode);
    }

    [Fact]
    public void Mode_is_SelectionPolygon_when_polygon_selection_present()
    {
        var doc = new Document(900, 600);
        doc.Selection = new PolygonSelection(
            new SKPoint(0, 0), new SKPoint(100, 0),
            new SKPoint(100, 100), new SKPoint(0, 100));
        Assert.Equal(DocumentMode.SelectionPolygon, doc.Mode);
    }

    [Fact]
    public void Mode_is_FloatingActive_when_pickup_present()
    {
        var doc = new Document(900, 600);
        doc.FloatingPickup = MakeDummyPickup();
        Assert.Equal(DocumentMode.FloatingActive, doc.Mode);
    }

    // —— Antipattern #1: ClearCanvasCommand resets all state layers ——
    // The actual ClearCanvasCommand is not implemented yet, but the invariant
    // it relies on (active layer paints white + floating disposed + selection null)
    // is testable here by exercising the underlying API.

    [Fact]
    public void CommitFloating_releases_pickup_and_returns_to_idle()
    {
        var doc = new Document(900, 600);
        doc.FloatingPickup = MakeDummyPickup();
        Assert.Equal(DocumentMode.FloatingActive, doc.Mode);

        doc.CommitFloating();

        Assert.Null(doc.FloatingPickup);
        Assert.Equal(DocumentMode.Idle, doc.Mode);
    }

    [Fact]
    public void CommitFloating_on_empty_document_is_noop()
    {
        var doc = new Document(900, 600);
        var ex = Record.Exception(() => doc.CommitFloating());
        Assert.Null(ex);
        Assert.Equal(DocumentMode.Idle, doc.Mode);
    }

    // —— Layer basics ——

    [Fact]
    public void New_document_has_one_white_background_layer()
    {
        var doc = new Document(20, 20);
        Assert.Single(doc.Layers);
        var bg = Assert.IsType<PixelLayer>(doc.Layers[0]);
        Assert.True(bg.IsAllWhite());
    }

    [Fact]
    public void PixelLayer_extract_region_copies_the_pixels()
    {
        var layer = new PixelLayer(20, 20, SKColors.White);
        // Paint a red square at (5..15, 5..15).
        using (var c = new SKCanvas(layer.Bitmap))
        using (var paint = new SKPaint { Color = SKColors.Red })
        {
            c.DrawRect(new SKRect(5, 5, 15, 15), paint);
        }

        using var extracted = layer.ExtractRegion(new SKRectI(5, 5, 15, 15));
        Assert.Equal(10, extracted.Width);
        Assert.Equal(10, extracted.Height);
        // Pixel at (0,0) of the extracted region is the red we drew.
        var px = extracted.GetPixel(0, 0);
        Assert.Equal(SKColors.Red, px);
    }
}
