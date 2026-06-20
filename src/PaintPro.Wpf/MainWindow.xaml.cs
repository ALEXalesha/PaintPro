using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PaintPro.Models;
using PaintPro.ViewModels;
using SkiaSharp;

namespace PaintPro;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // Drag-and-drop file open.
        AllowDrop = true;
        Drop += OnDrop;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Wire the canvas to the VM.
        Canvas.DataContext = Vm;

        Canvas.PixelPositionChanged += p =>
        {
            StatusPos.Text = p.HasValue ? $"X: {(int)p.Value.X}, Y: {(int)p.Value.Y}" : "—";
        };
        Canvas.PixelColorChanged += c =>
        {
            StatusHex.Text = $" #{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
        };

        // Ctrl+wheel zoom, centred on the cursor.
        PreviewMouseWheel += OnMouseWheel;

        // Text-tool prompt.
        Vm.TextRequested += pt =>
        {
            var s = Views.PromptDialog.Show("Введите текст:", "Текст", "", this);
            if (!string.IsNullOrEmpty(s)) Vm.ProvideText(s);
        };
    }

    // Ctrl+wheel zoom is now handled inside CanvasView (it owns the ScrollViewer
    // and centres the zoom on the cursor — see CanvasView.OnPreviewMouseWheel).
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Intentionally empty — CanvasView consumes the event.
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.Tag is string tag &&
            Enum.TryParse<ToolKind>(tag, out var kind))
        {
            Vm.ActiveTool = kind;
        }
    }

    private void OnPaletteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ViewModels.ColorEntryViewModel cv)
            Vm.PrimaryColor = cv.Color;
    }

    private void OnPickCustomColorClick(object sender, MouseButtonEventArgs e)
        => Vm.PickCustomColorCommand.Execute(null);

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnAboutClick(object sender, RoutedEventArgs e)
        => MessageBox.Show("Paint Pro 1.0 — C# + WPF + SkiaSharp.\nLiquid Glass build.", "О программе");

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length == 0) return;
        var bmp = Vm.FileService.OpenImage(files[0]);
        if (bmp is not null) Vm.ApplyOpenedBitmap(bmp);
    }

    /// <summary>Click on the layer name → make that layer active.</summary>
    private void OnLayerNameClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ViewModels.LayerListItemViewModel item)
            Vm.SetActiveLayerCommand.Execute(item);
    }

    /// <summary>Click on the layer delete (×) button.</summary>
    private void OnLayerDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ViewModels.LayerListItemViewModel item)
            Vm.RemoveLayerCommand.Execute(item);
    }
}
