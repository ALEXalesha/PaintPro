using CommunityToolkit.Mvvm.ComponentModel;
using PaintPro.Models;

namespace PaintPro.ViewModels;

/// <summary>
/// Per-layer row in the layer-panel ItemsControl.
/// Wraps a <see cref="Layer"/> so XAML can bind to Visible/Opacity/Name and
/// changes flow back to the underlying model.
/// </summary>
public partial class LayerListItemViewModel : ObservableObject
{
    public Layer Layer { get; }

    [ObservableProperty] private bool _visible;
    [ObservableProperty] private double _opacity;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isActive;

    public LayerListItemViewModel(Layer layer)
    {
        Layer = layer;
        _visible = layer.Visible;
        _opacity = layer.Opacity;
        _name = layer.Name;
    }

    partial void OnVisibleChanged(bool value) => Layer.Visible = value;
    partial void OnOpacityChanged(double value) => Layer.Opacity = (float)value;
    partial void OnNameChanged(string value) => Layer.Name = value;
}
