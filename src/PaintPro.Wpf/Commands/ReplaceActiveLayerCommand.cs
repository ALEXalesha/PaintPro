using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Replace the active layer's pixels (and, for rotation, the canvas size) with a
/// pre-computed result. Stores both the before and after bitmaps so the whole-layer
/// operations rotate / flip can be undone and redone. Whole snapshots are unavoidable
/// here — these ops touch every pixel, so a diff would be the whole layer anyway.
/// </summary>
public sealed class ReplaceActiveLayerCommand : IDocumentCommand
{
    private readonly SKBitmap _before;
    private readonly int _beforeW, _beforeH;
    private readonly SKBitmap _after;
    private readonly int _afterW, _afterH;

    /// <param name="before">Snapshot of the layer before the op (command takes ownership).</param>
    /// <param name="after">The transformed layer content (command takes ownership).</param>
    public ReplaceActiveLayerCommand(string displayName,
        SKBitmap before, int beforeW, int beforeH,
        SKBitmap after, int afterW, int afterH)
    {
        DisplayName = displayName;
        _before = before; _beforeW = beforeW; _beforeH = beforeH;
        _after = after; _afterW = afterW; _afterH = afterH;
    }

    public string DisplayName { get; }

    public void Execute(Document doc) => Install(doc, _after, _afterW, _afterH);
    public void Undo(Document doc) => Install(doc, _before, _beforeW, _beforeH);

    private static void Install(Document doc, SKBitmap content, int w, int h)
    {
        int idx = doc.ActiveLayerIndex;
        if (doc.Layers[idx] is not PixelLayer cur) return;
        var layer = new PixelLayer(w, h, SKColors.White)
        {
            Name = cur.Name,
            Visible = cur.Visible,
            Opacity = cur.Opacity,
        };
        using (var c = new SKCanvas(layer.Bitmap))
        {
            c.Clear(SKColors.White);
            c.DrawBitmap(content, 0, 0);
        }
        cur.Dispose();
        doc.Layers[idx] = layer;
        doc.CanvasWidth = w;
        doc.CanvasHeight = h;
    }
}
