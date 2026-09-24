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
        BuildThemeMenu();
        Loaded += OnLoaded;
        // Drag-and-drop file open.
        AllowDrop = true;
        Drop += OnDrop;
        Closing += OnClosing;
        // Обычные границы окна запоминаются, пока оно не развёрнуто: их и сохраним.
        LocationChanged += (_, _) => TrackNormalBounds();
        SizeChanged += (_, _) => TrackNormalBounds();
    }

    private Services.WindowPlacement.Area? _normalBounds;

    private void TrackNormalBounds()
    {
        if (WindowState == WindowState.Normal && IsLoaded)
            _normalBounds = Services.WindowPlacementService.CurrentBounds(this) ?? _normalBounds;
    }

    /// <summary>
    /// Closing the window used to discard unsaved work without a word. Ask first, and let
    /// the user back out — including when they cancel the save dialog itself.
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        AskAboutUnsaved(e);
        if (!e.Cancel) SavePlacement();
    }

    private void AskAboutUnsaved(System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsDirty) return;

        var answer = Views.GlassMessage.Show(
            "Рисунок изменён. Сохранить перед выходом?",
            "Paint Pro", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (answer == MessageBoxResult.Yes && !vm.TrySaveForClose()) e.Cancel = true;
    }

    /// <summary>Размер и место окна - в файл, чтобы следующий запуск открылся так же.</summary>
    private void SavePlacement()
    {
        var b = WindowState == WindowState.Normal ? Services.WindowPlacementService.CurrentBounds(this) ?? _normalBounds : _normalBounds;
        if (b is { } r)
            Services.WindowPlacementService.Save(new Services.WindowPlacement.Placement(r.X, r.Y, r.Width, r.Height, WindowState == WindowState.Maximized));
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
        Canvas.PixelColorChanged += c =>
        {
            StatusHex.Text = Services.ViewGeometry.HexLabel(c);
            // Квадрат - того же цвета, что подпись: точки под курсором, а не кисти.
            StatusSwatch.Visibility = c is null ? Visibility.Hidden : Visibility.Visible;
            if (c is { } color)
                StatusSwatch.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
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

    // ───── Свой заголовок окна ─────
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>
    /// Развёрнутое окно без системного заголовка выходит за экран на толщину невидимой
    /// рамки - края срезались бы; в развёрнутом виде рамка возвращается полем. Значок
    /// кнопки меняется на «восстановить».
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        var maximized = WindowState == WindowState.Maximized;
        var frame = SystemParameters.WindowResizeBorderThickness;
        RootGrid.Margin = maximized
            ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4)
            : new Thickness(0);
        MaximizeGlyph.Data = System.Windows.Media.Geometry.Parse(maximized
            ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z"
            : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
    }

    /// <summary>
    /// Скругление углов Windows 11: без системного заголовка оно пропадает, просим его явно.
    /// На Windows 10 вызова нет - там углы у всех окон прямые.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var round = 2; // DWMWCP_ROUND
        try { DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int)); }
        catch (EntryPointNotFoundException) { }
        // Размер и место с прошлого раза - до показа окна, чтобы оно не прыгало.
        _normalBounds = Services.WindowPlacementService.Restore(this);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Наполнить подменю тем. Список берётся из ThemeService: одно место на меню и на
    /// проверки, и добавить тему - значит добавить строку туда.
    /// </summary>
    private void BuildThemeMenu()
    {
        if (ThemeMenu is null) return;
        ThemeMenu.Items.Clear();
        foreach (var theme in Services.ThemeService.All)
        {
            var item = new MenuItem
            {
                Header = theme.Name,
                ToolTip = theme.Note,
                Tag = theme.Id,
                IsCheckable = true,
                IsChecked = theme.Id == Services.ThemeService.Current,
            };
            item.Click += OnThemeClick;
            ThemeMenu.Items.Add(item);
        }
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string id) return;
        var applied = Services.ThemeService.Apply(id);
        Services.ThemeService.Save(applied);
        // Галочка ставится по факту применённого, а не по нажатому: неизвестное имя
        // откатывается к исходной теме, и меню обязано показать правду.
        BuildThemeMenu();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
        => Views.GlassMessage.Show($"Paint Pro {MainViewModel.AppVersion} — C# + WPF + SkiaSharp.\nLiquid Glass build.",
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
