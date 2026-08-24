using PaintPro.Commands;
using PaintPro.Models;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Две вещи, которые документ обязан довести до конца сам, а не полагаться на то,
/// что вызывающий не забудет: «Создать» очищает весь документ, а не один слой, и
/// смена геометрии холста снимает выделение, чьи координаты она обесценила.
/// </summary>
public class CanvasGeometryAndClearTests
{
    private static Document MakeDocWithTwoLayers()
    {
        var doc = new Document(40, 30);
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Layer 1"), doc);
        // Рисуем по пятну на каждом слое.
        Paint(doc.Layers[0], SKColors.Red);
        Paint(doc.Layers[1], SKColors.Lime);
        return doc;
    }

    private static void Paint(Layer layer, SKColor color)
    {
        var pl = (PixelLayer)layer;
        using var c = new SKCanvas(pl.Bitmap);
        using var p = new SKPaint { Color = color, Style = SKPaintStyle.Fill, BlendMode = SKBlendMode.Src };
        c.DrawRect(new SKRect(5, 5, 20, 20), p);
    }

    private static SKColor At(Document doc, int index, int x, int y)
        => ((PixelLayer)doc.Layers[index]).Bitmap.GetPixel(x, y);

    [Fact]
    public void New_document_leaves_a_single_blank_paper_layer()
    {
        var doc = MakeDocWithTwoLayers();
        doc.ActiveLayerIndex = 0; // активен нижний

        doc.History.ExecuteAndPush(new ClearCanvasCommand(), doc);

        // От стопки остаётся бумага, и она чистая. Прежде верхние слои только чистились:
        // новый документ выходил с прежним числом слоёв и прежними их именами в панели.
        Assert.Single(doc.Layers);
        Assert.Equal(SKColors.White, At(doc, 0, 10, 10));
        Assert.Equal(0, doc.ActiveLayerIndex);
    }

    [Fact]
    public void Undo_of_new_document_brings_every_layer_back()
    {
        var doc = MakeDocWithTwoLayers();
        doc.ActiveLayerIndex = 0;

        doc.History.ExecuteAndPush(new ClearCanvasCommand(), doc);
        doc.History.Undo(doc);

        Assert.Equal(SKColors.Red, At(doc, 0, 10, 10));
        Assert.Equal(SKColors.Lime, At(doc, 1, 10, 10));
    }

    [Fact]
    public void Redo_of_new_document_blanks_the_document_again()
    {
        var doc = MakeDocWithTwoLayers();
        doc.History.ExecuteAndPush(new ClearCanvasCommand(), doc);
        doc.History.Undo(doc);
        doc.History.Redo(doc);

        Assert.Single(doc.Layers);
        Assert.Equal(SKColors.White, At(doc, 0, 10, 10));
    }

    [Fact]
    public void Rotating_the_document_drops_the_selection()
    {
        var doc = new Document(40, 30);
        doc.Selection = new RectSelection(5, 5, 20, 20);

        doc.History.ExecuteAndPush(DocumentTransform.Rotate(doc, MathF.PI / 2f), doc);

        // Холст стал 30×40, а рамка осталась бы висеть на координатах старого.
        Assert.Null(doc.Selection);
    }

    [Fact]
    public void Flipping_the_document_drops_the_selection()
    {
        var doc = new Document(40, 30);
        doc.Selection = new RectSelection(0, 0, 10, 10);

        doc.History.ExecuteAndPush(DocumentTransform.Flip(doc, horizontal: true), doc);

        Assert.Null(doc.Selection);
    }

    [Fact]
    public void Resizing_the_canvas_drops_the_selection()
    {
        var doc = new Document(40, 30);
        doc.Selection = new RectSelection(20, 20, 15, 8);

        doc.History.ExecuteAndPush(new ResizeCanvasCommand(12, 12, SKColors.White), doc);

        // Выделение целиком за пределами нового холста.
        Assert.Null(doc.Selection);
    }

    [Fact]
    public void Undoing_a_resize_also_leaves_no_stale_selection()
    {
        var doc = new Document(40, 30);
        doc.History.ExecuteAndPush(new ResizeCanvasCommand(12, 12, SKColors.White), doc);
        doc.Selection = new RectSelection(0, 0, 10, 10);

        doc.History.Undo(doc);

        Assert.Null(doc.Selection);
    }
}
