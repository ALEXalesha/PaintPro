using PaintPro.Models;

namespace PaintPro.Commands;

/// <summary>
/// Видимость и прозрачность одного слоя.
///
/// Флажок и ползунок в панели слоёв писали прямо в модель: Ctrl+Z их не отменял, а
/// <see cref="Services.HistoryManager.IsDirtySinceSave"/> считает правки по позиции в
/// истории и таких изменений не видел. При этом на файл они влияют - <see
/// cref="Services.FileService.Flatten"/> пропускает невидимые слои и учитывает
/// прозрачность, - и выключенный слой пропадал из картинки, а окно закрывалось молча.
///
/// Подряд идущие правки одного слоя склеиваются через <see cref="MergeInto"/>: ползунок
/// шлёт значение на каждый пиксель перетаскивания, и без склейки один жест давал бы
/// полсотни записей в истории.
/// </summary>
public sealed class LayerPropertyCommand : IDocumentCommand
{
    private readonly bool _visibleBefore;
    private readonly float _opacityBefore;
    private bool _visibleAfter;
    private float _opacityAfter;

    public LayerPropertyCommand(Layer layer, bool visible, float opacity)
    {
        LayerId = layer.Id;
        _visibleBefore = layer.Visible;
        _opacityBefore = layer.Opacity;
        _visibleAfter = visible;
        _opacityAfter = opacity;
    }

    public Guid LayerId { get; }

    public string DisplayName => "Layer properties";

    public void Execute(Document doc) => Apply(doc, _visibleAfter, _opacityAfter);
    public void Undo(Document doc) => Apply(doc, _visibleBefore, _opacityBefore);

    /// <summary>Дописать в эту же запись следующее значение того же жеста.</summary>
    public void MergeInto(Document doc, bool visible, float opacity)
    {
        _visibleAfter = visible;
        _opacityAfter = opacity;
        Apply(doc, visible, opacity);
    }

    /// <summary>Правка ничего не поменяла: записывать нечего.</summary>
    public bool ChangedAnything
        => _visibleBefore != _visibleAfter || Math.Abs(_opacityBefore - _opacityAfter) > 0.0001f;

    private void Apply(Document doc, bool visible, float opacity)
    {
        foreach (var l in doc.Layers)
        {
            if (l.Id != LayerId) continue;
            l.Visible = visible;
            l.Opacity = opacity;
            return;
        }
    }
}
