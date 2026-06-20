using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PaintPro.Services;
using SkiaSharp;

namespace PaintPro.ViewModels;

/// <summary>Single colour swatch in the palette / recent-colours rows.</summary>
public partial class ColorEntryViewModel : ObservableObject
{
    [ObservableProperty] private SKColor _color;
    public SolidColorBrush Brush => Color.ToBrush();
    public ColorEntryViewModel(SKColor c) { Color = c; }
}
