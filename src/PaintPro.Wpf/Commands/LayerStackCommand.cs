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
        // Добавление выбирает новый слой: пользователь только что попросил его завести и
        // сейчас будет по нему рисовать. Повтор удаления - наоборот, возвращает активность
        // туда, куда её увело само удаление.
        if (_isAdd) Insert(doc, selectRestored: true); else Delete(doc);
    }

    public void Undo(Document doc)
    {
        // Отмена удаления возвращает документ таким, каким он был ДО удаления, - вместе с
        // тем, какой слой был активен. Пока активным становился восстановленный, Ctrl+Z по
        // удалению уводил кисть на другой слой: пользователь удалил верхний слой, вернул
        // его отменой и продолжал рисовать - но уже не там, где рисовал минуту назад, а по
        // только что воскресшему. Номер активного слоя удаление и записывает.
        if (_isAdd) Delete(doc); else Insert(doc, selectRestored: false);
    }

    private void Insert(Document doc, bool selectRestored)
    {
        if (_index < 0 || _index > doc.Layers.Count) return;
        // Слой строится под ТЕКУЩИЙ холст, а не под тот, что был при записи команды.
        // Размер холста меняют поворот, кадрирование, открытие файла и смена размера, и
        // слой, восстановленный по старым числам, оказывался меньше остальных: рисовать по
        // нему за его краем было нельзя, а сохранённая картинка обрезалась по нему молча.
        // Свои размеры остаются запасным вариантом на случай пустого документа.
        int w = doc.CanvasWidth > 0 ? doc.CanvasWidth : _width;
        int h = doc.CanvasHeight > 0 ? doc.CanvasHeight : _height;
        var layer = new PixelLayer(w, h, SKColors.Transparent)
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
        doc.ActiveLayerIndex = selectRestored
            ? _index
            : Math.Clamp(_previousActiveIndex, 0, doc.Layers.Count - 1);
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
