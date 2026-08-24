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
/// supposedly blank canvas — and writing it to disk on the next Ctrl+S.
///
/// От стопки остаётся один слой - бумага. Чистить слои по месту было мало: «Создать»
/// оставляло документ с прежним числом слоёв, панель показывала «Layer 1», «Layer 2» и
/// прочее, чего в новом документе взяться неоткуда, а следующий «Добавить слой» получал
/// имя «Layer 3». Новый документ - это чистый лист, а не старый со стёртым рисунком.
/// Отмена возвращает стопку целиком: слои строятся заново с прежними идентификаторами,
/// так что записи истории, сделанные по ним, снова начинают находить свою цель.
/// </summary>
public sealed class ClearCanvasCommand : IDocumentCommand, IDisposable
{
    /// <summary>Слой на момент очистки: всё, что нужно, чтобы собрать его обратно.</summary>
    private sealed record LayerShot(Guid Id, string Name, bool Visible, float Opacity, SKBitmap Content);

    /// <summary>Имя слоя-бумаги в новом документе - то же, что даёт ему конструктор <see cref="Document"/>.</summary>
    public const string DefaultPaperName = "Background";

    private readonly SKColor _fill;
    private LayerShot[]? _previousLayers;
    private Selection? _previousSelection;
    private int _previousActiveIndex;

    public ClearCanvasCommand(SKColor? fill = null) => _fill = fill ?? SKColors.White;

    public string DisplayName => "Clear canvas";

    public long ApproximateBytes
    {
        get
        {
            if (_previousLayers is null) return 0;
            long sum = 0;
            foreach (var s in _previousLayers) sum += (long)s.Content.RowBytes * s.Content.Height;
            return sum;
        }
    }

    public void Dispose()
    {
        if (_previousLayers is null) return;
        foreach (var s in _previousLayers) s.Content.Dispose();
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
        _previousActiveIndex = doc.ActiveLayerIndex;
        _previousLayers ??= Snapshot(doc);

        // Бумагу чистим, остальное убираем. Коллекцию не опустошаем ни на миг:
        // Document.ActiveLayer читает Layers[0] и на пустой стопке падает, а панель
        // слоёв пересобирается на каждое изменение коллекции.
        if (doc.Layers[0] is PixelLayer paper)
        {
            paper.Clear(_fill);
            // Имя, видимость и прозрачность - такая же часть слоя, как пиксели. Пока
            // стирались одни пиксели, «Создать» отдавало новый документ, у которого
            // бумага звалась как в прошлой работе, а то и была спрятана или выкручена в
            // прозрачность: пользователь рисовал по чистому листу и не видел ни штриха,
            // а в файл уходил белый прямоугольник. Возвращает всё это назад отмена -
            // слои она собирает из снимка целиком.
            paper.Name = DefaultPaperName;
            paper.Visible = true;
            paper.Opacity = 1f;
        }
        Shrink(doc, 1);
        doc.ActiveLayerIndex = 0;
        doc.Selection = null;
    }

    public void Undo(Document doc)
    {
        if (_previousLayers is { } shots)
        {
            for (int i = 0; i < shots.Length; i++)
            {
                var layer = Rebuild(shots[i], doc.CanvasWidth, doc.CanvasHeight);
                if (i < doc.Layers.Count)
                {
                    var old = doc.Layers[i];
                    doc.Layers[i] = layer;
                    old.Dispose();
                }
                else doc.Layers.Add(layer);
            }
            Shrink(doc, shots.Length);
            doc.ActiveLayerIndex = Math.Clamp(_previousActiveIndex, 0, doc.Layers.Count - 1);
        }
        doc.Selection = _previousSelection;
    }

    /// <summary>Оставить в стопке первые <paramref name="count"/> слоёв, освободив остальные.</summary>
    private static void Shrink(Document doc, int count)
    {
        while (doc.Layers.Count > count)
        {
            var last = doc.Layers[^1];
            doc.Layers.RemoveAt(doc.Layers.Count - 1);
            last.Dispose();
        }
    }

    /// <summary>
    /// Слой из снимка. Размер берётся у ТЕКУЩЕГО холста, а не у снимка: между очисткой и
    /// отменой холст мог сменить размер, и слой по старым числам оказался бы меньше
    /// остальных - ровно та же поправка, что в <see cref="LayerStackCommand"/>.
    /// </summary>
    private static PixelLayer Rebuild(LayerShot shot, int width, int height)
    {
        var layer = new PixelLayer(Math.Max(1, width), Math.Max(1, height), SKColors.Transparent)
        {
            Id = shot.Id,
            Name = shot.Name,
            Visible = shot.Visible,
            Opacity = shot.Opacity,
        };
        using var c = new SKCanvas(layer.Bitmap);
        c.DrawBitmap(shot.Content, 0, 0);
        return layer;
    }

    private static LayerShot[] Snapshot(Document doc)
    {
        var shots = new LayerShot[doc.Layers.Count];
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            var l = doc.Layers[i];
            var content = l is PixelLayer pl
                ? pl.ExtractRegion(new SKRectI(0, 0, pl.Width, pl.Height))
                : new SKBitmap(1, 1);
            shots[i] = new LayerShot(l.Id, l.Name, l.Visible, l.Opacity, content);
        }
        return shots;
    }
}
