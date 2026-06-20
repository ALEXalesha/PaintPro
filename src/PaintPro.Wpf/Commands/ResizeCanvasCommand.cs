using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Resize the document's canvas, preserving existing pixels (top-left anchored).
/// New area is filled with the background colour.
/// </summary>
public sealed class ResizeCanvasCommand : IDocumentCommand
{
    private readonly int _newWidth;
    private readonly int _newHeight;
    private readonly SKColor _backgroundColor;

    private int _previousWidth;
    private int _previousHeight;
    private SKBitmap? _previousLayerBitmap;

    public ResizeCanvasCommand(int newWidth, int newHeight, SKColor? backgroundColor = null)
    {
        _newWidth = Math.Max(1, newWidth);
        _newHeight = Math.Max(1, newHeight);
        _backgroundColor = backgroundColor ?? SKColors.White;
    }

    public string DisplayName => "Resize canvas";

    public void Execute(Document doc)
    {
        _previousWidth = doc.CanvasWidth;
        _previousHeight = doc.CanvasHeight;

        for (int i = 0; i < doc.Layers.Count; i++)
        {
            if (doc.Layers[i] is not PixelLayer old) continue;
            if (i == doc.ActiveLayerIndex)
                _previousLayerBitmap = old.ExtractRegion(new SKRectI(0, 0, old.Width, old.Height));

            var newLayer = new PixelLayer(_newWidth, _newHeight, _backgroundColor)
            {
                Name = old.Name,
                Visible = old.Visible,
                Opacity = old.Opacity,
            };
            using (var canvas = new SKCanvas(newLayer.Bitmap))
            {
                canvas.DrawBitmap(old.Bitmap, 0, 0);
            }
            old.Dispose();
            doc.Layers[i] = newLayer;
        }

        doc.CanvasWidth = _newWidth;
        doc.CanvasHeight = _newHeight;
    }

    public void Undo(Document doc)
    {
        // Recreate the previous-size layers from the snapshot.
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            if (doc.Layers[i] is not PixelLayer cur) continue;
            var restored = new PixelLayer(_previousWidth, _previousHeight, _backgroundColor)
            {
                Name = cur.Name,
                Visible = cur.Visible,
                Opacity = cur.Opacity,
            };
            if (i == doc.ActiveLayerIndex && _previousLayerBitmap is not null)
            {
                using var canvas = new SKCanvas(restored.Bitmap);
                canvas.DrawBitmap(_previousLayerBitmap, 0, 0);
            }
            else
            {
                using var canvas = new SKCanvas(restored.Bitmap);
                canvas.DrawBitmap(cur.Bitmap, 0, 0);
            }
            cur.Dispose();
            doc.Layers[i] = restored;
        }
        doc.CanvasWidth = _previousWidth;
        doc.CanvasHeight = _previousHeight;
    }
}
