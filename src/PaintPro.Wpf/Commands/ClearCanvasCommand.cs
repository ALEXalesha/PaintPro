using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Blank the document: every layer, plus any pending floating pickup and selection.
/// Directly addresses antipattern #1 from the spec — every transient state slice must be
/// reset, not just the visible bitmap. Поднятый пикап не сохраняется в команде, а
/// возвращается на холст до снимка: отмена должна вернуть документ таким, каким он был
/// до подъёма, а не с дырой на его месте.
///
/// Every layer, not the active one. This is what «Файл → Создать» runs, and clearing only
/// the active layer left a document with two layers showing the old drawing through a
/// supposedly blank canvas — and writing it to disk on the next Ctrl+S. The bottom layer
/// goes back to the background colour because it is the paper; the ones above go to
/// transparent, same rule as <see cref="ResizeCanvasCommand"/>.
/// </summary>
public sealed class ClearCanvasCommand : IDocumentCommand, IDisposable
{
    private readonly SKColor _fill;
    private SKBitmap[]? _previousLayers;
    private Selection? _previousSelection;

    public ClearCanvasCommand(SKColor? fill = null) => _fill = fill ?? SKColors.White;

    public string DisplayName => "Clear canvas";

    public long ApproximateBytes
    {
        get
        {
            if (_previousLayers is null) return 0;
            long sum = 0;
            foreach (var b in _previousLayers) sum += (long)b.RowBytes * b.Height;
            return sum;
        }
    }

    public void Dispose()
    {
        if (_previousLayers is null) return;
        foreach (var b in _previousLayers) b.Dispose();
        _previousLayers = null;
    }

    public void Execute(Document doc)
    {
        // Поднятые пиксели возвращаем на место ДО снимка. Прежняя версия просто клала
        // сам пикап в поле команды: отмена возвращала холст с дырой на месте подъёма, а
        // объект с двумя битмапами оставался жить в записи истории и не освобождался
        // вовсе, если её вытесняло переполнение.
        doc.CancelFloating();
        _previousSelection = doc.Selection;
        _previousLayers ??= Snapshot(doc);
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            if (doc.Layers[i] is PixelLayer pl)
                pl.Clear(i == 0 ? _fill : SKColors.Transparent);
        }
        doc.Selection = null;
    }

    public void Undo(Document doc)
    {
        if (_previousLayers is { } shots)
        {
            for (int i = 0; i < doc.Layers.Count && i < shots.Length; i++)
            {
                if (doc.Layers[i] is not PixelLayer pl) continue;
                using var canvas = new SKCanvas(pl.Bitmap);
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(shots[i], 0, 0);
            }
        }
        doc.Selection = _previousSelection;
    }

    private static SKBitmap[] Snapshot(Document doc)
    {
        var shots = new SKBitmap[doc.Layers.Count];
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            shots[i] = doc.Layers[i] is PixelLayer pl
                ? pl.ExtractRegion(new SKRectI(0, 0, pl.Width, pl.Height))
                : new SKBitmap(1, 1);
        }
        return shots;
    }
}
