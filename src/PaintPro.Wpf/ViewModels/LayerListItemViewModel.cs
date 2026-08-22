using CommunityToolkit.Mvvm.ComponentModel;
using PaintPro.Models;

namespace PaintPro.ViewModels;

/// <summary>
/// Per-layer row in the layer-panel ItemsControl.
/// Wraps a <see cref="Layer"/> so XAML can bind to Visible/Opacity/Name.
///
/// Видимость и прозрачность строка сама в модель не пишет: их отдаёт наружу
/// <see cref="Apply"/>, а вьюмодель заворачивает правку в
/// <see cref="Commands.LayerPropertyCommand"/>. Иначе Ctrl+Z их не отменяет, а признак
/// несохранённой работы о них не знает - при том, что в файл они попадают.
/// </summary>
public partial class LayerListItemViewModel : ObservableObject
{
    public Layer Layer { get; }

    /// <summary>Куда уходит правка: (слой, видимость, прозрачность).</summary>
    private readonly Action<Layer, bool, float>? _apply;

    /// <summary>True, пока значения ставятся из модели: обратная запись тогда не нужна.</summary>
    private bool _syncing;

    [ObservableProperty] private bool _visible;
    [ObservableProperty] private double _opacity;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isActive;

    public LayerListItemViewModel(Layer layer, Action<Layer, bool, float>? apply = null)
    {
        Layer = layer;
        _apply = apply;
        _syncing = true;
        _visible = layer.Visible;
        _opacity = layer.Opacity;
        _name = layer.Name;
        _syncing = false;
    }

    partial void OnVisibleChanged(bool value) => Push(value, (float)Opacity);
    partial void OnOpacityChanged(double value) => Push(Visible, (float)value);
    partial void OnNameChanged(string value) => Layer.Name = value;

    private void Push(bool visible, float opacity)
    {
        if (_syncing) return;
        if (_apply is null)
        {
            Layer.Visible = visible;
            Layer.Opacity = opacity;
            return;
        }
        _apply(Layer, visible, opacity);
    }
}
