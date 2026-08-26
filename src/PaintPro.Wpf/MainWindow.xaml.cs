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
        Closing += OnClosing;
    }

    /// <summary>
    /// Closing the window used to discard unsaved work without a word. Ask first, and let
    /// the user back out — including when they cancel the save dialog itself.
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsDirty) return;

        var answer = MessageBox.Show(
            "Рисунок изменён. Сохранить перед выходом?",
            "Paint Pro", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (answer == MessageBoxResult.Yes && !vm.TrySaveForClose()) e.Cancel = true;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    /// <summary>
    /// Подписки ставятся один раз. Loaded приходит при каждом возвращении окна в дерево
    /// визуалов, а обработчики здесь складываются: второй подписчик на TextRequested -
    /// это два окна ввода текста подряд на один клик.
    /// </summary>
    private bool _wired;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Wire the canvas to the VM.
        Canvas.DataContext = Vm;
        if (_wired) return;
        _wired = true;

        // Сами строки собирает ViewGeometry: их формат проверяется тестами, а code-behind
        // остаётся тем, чем должен быть, - раскладкой готового по элементам.
        Canvas.PixelPositionChanged += p => StatusPos.Text = Services.ViewGeometry.PositionLabel(p);
        Canvas.PixelColorChanged += c => StatusHex.Text = Services.ViewGeometry.HexLabel(c);

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
        => MessageBox.Show($"Paint Pro {MainViewModel.AppVersion} — C# + WPF + SkiaSharp.\nLiquid Glass build.",
                           "О программе");

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length == 0) return;
        // Ошибка чтения доходит до пользователя: раньше перетаскивание папки или битого
        // файла не делало вообще ничего.
        Vm.OpenPath(files[0]);
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
