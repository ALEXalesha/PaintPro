using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Add or remove a layer, undoably.
///
/// Deleting a layer used to be permanent: the object was disposed on the spot and Ctrl+Z
/// had nothing to bring back. The command keeps the pixels and reinstates the layer with
/// its original <see cref="Layer.Id"/>, so history entries recorded against that layer
/// start resolving again after an undo.
/// </summary>
public sealed class LayerStackCommand : IDocumentCommand, IDisposable
{
    private readonly bool _isAdd;
    private readonly int _index;
    private readonly Guid _layerId;
    private readonly string _name;
    private readonly int _width, _height;
    private bool _visible;
    private float _opacity;
    private SKBitmap? _content;
    private int _previousActiveIndex;

    private LayerStackCommand(bool isAdd, int index, Guid layerId, string name,
        int width, int height, bool visible, float opacity, SKBitmap? content)
    {
        _isAdd = isAdd;
        _index = index;
        _layerId = layerId;
        _name = name;
        _width = width;
        _height = height;
        _visible = visible;
        _opacity = opacity;
        _content = content;
    }

    /// <summary>Append an empty transparent layer on top of the stack.</summary>
    public static LayerStackCommand Add(Document doc, string name)
        => new(true, doc.Layers.Count, Guid.NewGuid(), name,
               doc.CanvasWidth, doc.CanvasHeight, visible: true, opacity: 1f, content: null);

    /// <summary>Remove <paramref name="layer"/>, keeping a copy of its pixels for undo.</summary>
    public static LayerStackCommand Remove(Document doc, PixelLayer layer)
        => new(false, doc.Layers.IndexOf(layer), layer.Id, layer.Name,
               layer.Width, layer.Height, layer.Visible, layer.Opacity,
               layer.ExtractRegion(new SKRectI(0, 0, layer.Width, layer.Height)));

    public string DisplayName => _isAdd ? "Add layer" : "Remove layer";

    public long ApproximateBytes => Bytes(_content);

    private static long Bytes(SKBitmap? b) => b is null ? 0 : (long)b.RowBytes * b.Height;

    public void Dispose()
    {
        _content?.Dispose();
        _content = null;
    }

    public void Execute(Document doc)
    {
        if (_isAdd) Insert(doc); else Delete(doc);
    }

    public void Undo(Document doc)
    {
        if (_isAdd) Delete(doc); else Insert(doc);
    }

    private void Insert(Document doc)
    {
        if (_index < 0 || _index > doc.Layers.Count) return;
        var layer = new PixelLayer(_width, _height, SKColors.Transparent)
        {
            Id = _layerId,
            Name = _name,
            Visible = _visible,
            Opacity = _opacity,
        };
        if (_content is not null)
        {
            using var c = new SKCanvas(layer.Bitmap);
            c.DrawBitmap(_content, 0, 0);
        }
        doc.Layers.Insert(_index, layer);
        doc.ActiveLayerIndex = _index;
    }

    private void Delete(Document doc)
    {
        if (doc.FindPixelLayer(_layerId) is not { } layer) return;
        int idx = doc.Layers.IndexOf(layer);
        if (idx < 0 || doc.Layers.Count <= 1) return;

        // Re-capture on the way out: the layer may have been painted on since it was
        // created, and an add that gets undone still has to be redoable with those pixels.
        _content?.Dispose();
        _content = layer.ExtractRegion(new SKRectI(0, 0, layer.Width, layer.Height));
        _visible = layer.Visible;
        _opacity = layer.Opacity;

        _previousActiveIndex = doc.ActiveLayerIndex;
        layer.Dispose();
        doc.Layers.RemoveAt(idx);
        doc.ActiveLayerIndex = Math.Clamp(
            _previousActiveIndex >= idx ? _previousActiveIndex - 1 : _previousActiveIndex,
            0, doc.Layers.Count - 1);
    }
}
