using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Swap the pixel content of every layer at once and resize the canvas with it.
/// This is what rotate / flip / crop need: they change the document, not one layer, and
/// the previous per-layer version left the other layers at the old size while the canvas
/// reported the new one.
///
/// Whole snapshots are unavoidable here — these ops touch every pixel, so a diff would be
/// the whole layer anyway. Layer identity, name, visibility and opacity are preserved so
/// history commands recorded against those layers keep resolving.
/// </summary>
public sealed class ReplaceAllLayersCommand : IDocumentCommand, IDisposable
{
    private readonly SKBitmap[] _before;
    private readonly int _beforeW, _beforeH;
    private readonly SKBitmap[] _after;
    private readonly int _afterW, _afterH;

    /// <param name="before">One snapshot per layer, in layer order (command takes ownership).</param>
    /// <param name="after">Transformed content per layer, same order (command takes ownership).</param>
    public ReplaceAllLayersCommand(string displayName,
        SKBitmap[] before, int beforeW, int beforeH,
        SKBitmap[] after, int afterW, int afterH)
    {
        DisplayName = displayName;
        _before = before; _beforeW = beforeW; _beforeH = beforeH;
        _after = after; _afterW = afterW; _afterH = afterH;
    }

    public string DisplayName { get; }

    // The heaviest command in the app: two full copies of every layer.
    public long ApproximateBytes => Total(_before) + Total(_after);

    private static long Total(SKBitmap[] set)
    {
        long sum = 0;
        foreach (var b in set) sum += (long)b.RowBytes * b.Height;
        return sum;
    }

    public void Dispose()
    {
        foreach (var b in _before) b.Dispose();
        foreach (var b in _after) b.Dispose();
    }

    public void Execute(Document doc) => Install(doc, _after, _afterW, _afterH);
    public void Undo(Document doc) => Install(doc, _before, _beforeW, _beforeH);

    private static void Install(Document doc, SKBitmap[] content, int w, int h)
    {
        if (content.Length != doc.Layers.Count) return;

        for (int i = 0; i < doc.Layers.Count; i++)
        {
            if (doc.Layers[i] is not PixelLayer cur) continue;
            var layer = new PixelLayer(w, h)
            {
                Id = cur.Id,
                Name = cur.Name,
                Visible = cur.Visible,
                Opacity = cur.Opacity,
            };
            using (var c = new SKCanvas(layer.Bitmap)) c.DrawBitmap(content[i], 0, 0);
            cur.Dispose();
            doc.Layers[i] = layer;
        }

        doc.CanvasWidth = w;
        doc.CanvasHeight = h;
        // Поворот, отражение и кадрирование переставляют пиксели под выделением, а
        // поворот ещё и меняет размер холста. Рамка на старых координатах указывает уже
        // не на то, что пользователь выделял. Снимаем здесь, чтобы это работало и при
        // откате по истории. В Electron-версии то же самое сделано в 1.4.0.
        doc.Selection = null;
    }
}
