using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using SkiaSharp;

namespace PaintPro.ViewModels;

/// <summary>
/// Root ViewModel. Owns the Document, ToolContext, and the registry of ITool instances.
/// All RelayCommand methods translate UI actions → model edits via the Command Pattern.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public Document Document { get; }
    public ToolContext ToolContext { get; }
    public FileService FileService { get; } = new();
    public ClipboardService ClipboardService { get; } = new();

    private readonly Dictionary<ToolKind, ITool> _tools;
    private readonly PickerTool _pickerTool = new();
    private readonly TextTool _textTool = new();

    /// <summary>Raised when canvas needs an immediate visual refresh (rare; usually PropertyChanged handles it).</summary>
    public event Action? InvalidateCanvas;
    /// <summary>Raised when the TextTool wants a string from the user at <see cref="SKPoint"/>.</summary>
    public event Action<SKPoint>? TextRequested;

    /// <summary>VM-wrapped layer rows; rebuilt whenever Document.Layers changes.</summary>
    public ObservableCollection<LayerListItemViewModel> LayerItems { get; } = new();

    public MainViewModel()
    {
        Document = new Document(900, 600);
        ToolContext = new ToolContext(Document)
        {
            PrimaryColor = SKColors.Black,
            ToolSize = 4f,
            Opacity = 1f,
        };
        _pickerTool.ColorPicked += c => PrimaryColor = c;
        _textTool.TextRequested += p => TextRequested?.Invoke(p);

        _tools = new Dictionary<ToolKind, ITool>
        {
            [ToolKind.Pencil]  = new PencilTool(),
            [ToolKind.Brush]   = new BrushTool(),
            [ToolKind.Marker]  = new MarkerTool(),
            [ToolKind.Eraser]  = new EraserTool(),
            [ToolKind.Fill]    = new FillTool(),
            [ToolKind.Picker]  = _pickerTool,
            [ToolKind.Text]    = _textTool,
            [ToolKind.Line]    = new LineShapeTool(),
            [ToolKind.Rect]    = new RectShapeTool(),
            [ToolKind.Ellipse] = new EllipseShapeTool(),
            [ToolKind.Triangle]= new TriangleShapeTool(),
            [ToolKind.Star]    = new StarShapeTool(),
            [ToolKind.Arrow]   = new ArrowShapeTool(),
            [ToolKind.Heart]   = new HeartShapeTool(),
            [ToolKind.Select]  = new SelectTool(),
            [ToolKind.Quad]    = new QuadTool(),
            [ToolKind.Crop]    = new CropTool(),
            [ToolKind.Hand]    = new HandTool(),
        };
        ActiveToolInstance = _tools[ActiveTool];
        ActiveToolInstance.OnActivate(ToolContext);

        InitPalette();
        RebuildLayerItems();
        Document.Layers.CollectionChanged += (_, _) => RebuildLayerItems();
        Document.History.PropertyChanged += (_, _) =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };
        Document.History.Changed += RebuildHistoryItems;
        RebuildHistoryItems();
        Document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Document.ActiveLayerIndex)) RebuildLayerItems();
            // Selection/floating changes trigger overlay redraws; the CanvasView listens for these.
            if (e.PropertyName is nameof(Document.Selection) or nameof(Document.FloatingPickup)
                or nameof(Document.CanvasWidth) or nameof(Document.CanvasHeight))
            {
                InvalidateCanvas?.Invoke();
            }
        };
    }

    private void RebuildLayerItems()
    {
        LayerItems.Clear();
        for (int i = 0; i < Document.Layers.Count; i++)
        {
            var item = new LayerListItemViewModel(Document.Layers[i], ApplyLayerProperties)
            {
                IsActive = i == Document.ActiveLayerIndex,
            };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(LayerListItemViewModel.Visible)
                    or nameof(LayerListItemViewModel.Opacity))
                    InvalidateCanvas?.Invoke();
            };
            LayerItems.Add(item);
        }
    }

    /// <summary>
    /// Правка видимости/прозрачности слоя из панели - через историю.
    /// Подряд идущие правки одного слоя дописываются в ту же запись: перетаскивание
    /// ползунка иначе оставляет по записи на каждый его пиксель.
    /// </summary>
    private void ApplyLayerProperties(Layer layer, bool visible, float opacity)
    {
        if (layer.Visible == visible && Math.Abs(layer.Opacity - opacity) < 0.0001f) return;

        if (Document.History.Current is LayerPropertyCommand last && last.LayerId == layer.Id)
        {
            last.MergeInto(Document, visible, opacity);
        }
        else
        {
            var cmd = new LayerPropertyCommand(layer, visible, opacity);
            if (!cmd.ChangedAnything) return;
            Document.History.ExecuteAndPush(cmd, Document);
        }
        InvalidateCanvas?.Invoke();
    }

    [RelayCommand]
    private void AddLayer()
    {
        Document.CommitFloating();
        Document.History.ExecuteAndPush(
            LayerStackCommand.Add(Document, $"Layer {Document.Layers.Count}"), Document);
        InvalidateCanvas?.Invoke();
    }

    [RelayCommand]
    private void RemoveLayer(LayerListItemViewModel? item)
    {
        if (item is null || Document.Layers.Count <= 1) return;
        if (item.Layer is not PixelLayer pl || Document.Layers.IndexOf(pl) < 0) return;
        Document.CommitFloating();
        Document.History.ExecuteAndPush(LayerStackCommand.Remove(Document, pl), Document);
        InvalidateCanvas?.Invoke();
    }

    [RelayCommand]
    private void SetActiveLayer(LayerListItemViewModel? item)
    {
        if (item is null) return;
        var idx = Document.Layers.IndexOf(item.Layer);
        if (idx >= 0) Document.ActiveLayerIndex = idx;
    }

    // ───────── Tool selection ─────────
    [ObservableProperty] private ToolKind _activeTool = ToolKind.Pencil;
    public ITool ActiveToolInstance { get; private set; } = null!;

    partial void OnActiveToolChanged(ToolKind value)
    {
        // Deactivate the old tool (commits any pending floating/state).
        ActiveToolInstance?.OnDeactivate(ToolContext);
        ActiveToolInstance = _tools[value];
        ActiveToolInstance.OnActivate(ToolContext);
        InvalidateCanvas?.Invoke();
    }

    [RelayCommand] private void SelectTool(string name)
    {
        if (Enum.TryParse<ToolKind>(name, out var k)) ActiveTool = k;
    }

    // ───────── Drawing state ─────────
    [ObservableProperty] private SKColor _primaryColor = SKColors.Black;
    partial void OnPrimaryColorChanged(SKColor value)
    {
        ToolContext.PrimaryColor = value;
        PrimaryColorBrush = new SolidColorBrush(value.ToWpf());
        AddRecentColor(value);
    }

    [ObservableProperty] private SolidColorBrush _primaryColorBrush = new(Colors.Black);

    [ObservableProperty] private int _toolSize = 4;
    partial void OnToolSizeChanged(int value) => ToolContext.ToolSize = Math.Max(1, value);

    [ObservableProperty] private double _opacity = 1.0;
    partial void OnOpacityChanged(double value)
        => ToolContext.Opacity = (float)Math.Clamp(value, 0, 1);

    /// <summary>Outline-only vs filled for the shape tools (rect, ellipse, triangle, star, heart).</summary>
    [ObservableProperty] private bool _shapeFill;
    partial void OnShapeFillChanged(bool value)
    {
        foreach (var tool in _tools.Values)
            if (tool is ShapeTool shape) shape.Fill = value;
    }

    // ───────── Zoom / pan ─────────
    [ObservableProperty] private double _zoom = 1.0;
    [RelayCommand] private void ZoomIn()  => Zoom = GeometryMath.NextZoomStep((float)Zoom, true);
    [RelayCommand] private void ZoomOut() => Zoom = GeometryMath.NextZoomStep((float)Zoom, false);
    [RelayCommand] private void ZoomReset() => Zoom = 1.0;

    // ───────── Palette / recent colours ─────────
    public ObservableCollection<ColorEntryViewModel> Palette { get; } = new();
    public ObservableCollection<ColorEntryViewModel> RecentColors { get; } = new();

    private void InitPalette()
    {
        var hexes = new[]
        {
            "#000000","#404040","#808080","#C0C0C0","#FFFFFF",
            "#7F0000","#FF0000","#FF7F00","#FFC800","#FFFF00",
            "#7FFF00","#00FF00","#00FF7F","#00FFFF","#007FFF",
            "#0000FF","#7F00FF","#FF00FF","#FF007F","#A0522D",
            "#5B8DEF","#9D5BEF","#EF5B6E","#EFC85B","#5BEFA3",
            "#1A1330","#0E0C1A","#6E7080", // mid-grey-ish
            "#F4F4F8","#9EA0AE","#3CC8FF",
            "#FF64B4","#64FFB4","#B464FF","#FFB464",
            "#FFEDA0","#FED976","#FEB24C","#FD8D3C",
            "#FC4E2A","#E31A1C","#BD0026","#800026",
        };
        foreach (var h in hexes)
        {
            if (SKColor.TryParse(h, out var c)) Palette.Add(new ColorEntryViewModel(c));
        }
    }

    private void AddRecentColor(SKColor c)
    {
        var existing = RecentColors.FirstOrDefault(x => x.Color == c);
        if (existing is not null) RecentColors.Remove(existing);
        RecentColors.Insert(0, new ColorEntryViewModel(c));
        while (RecentColors.Count > 8) RecentColors.RemoveAt(RecentColors.Count - 1);
    }

    [RelayCommand] private void PickPaletteColor(ColorEntryViewModel? entry)
    {
        if (entry is null) return;
        PrimaryColor = entry.Color;
    }

    [RelayCommand] private void PickCustomColor()
    {
        var hex = $"#{PrimaryColor.Red:X2}{PrimaryColor.Green:X2}{PrimaryColor.Blue:X2}";
        var result = Views.PromptDialog.Show(
            "Введите цвет в HEX (например, #5B8DEF):", "Выбор цвета", hex);
        if (string.IsNullOrWhiteSpace(result)) return;
        if (SKColor.TryParse(result, out var c)) PrimaryColor = c;
    }

    // ───────── History ─────────
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() { Document.History.Undo(Document); AfterHistoryWalk(); }
    private bool CanUndo => Document.History.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() { Document.History.Redo(Document); AfterHistoryWalk(); }
    private bool CanRedo => Document.History.CanRedo;

    /// <summary>
    /// Откат мог поменять видимость и прозрачность слоя, а строки панели держат свои копии
    /// этих значений. Пересобираем их, иначе флажок показывает одно, а холст рисует другое.
    /// </summary>
    private void AfterHistoryWalk()
    {
        RebuildLayerItems();
        InvalidateCanvas?.Invoke();
    }

    /// <summary>Timeline rows for the History panel; rebuilt whenever the history changes.</summary>
    public ObservableCollection<HistoryEntryViewModel> HistoryItems { get; } = new();

    [RelayCommand]
    private void JumpToHistory(HistoryEntryViewModel? entry)
    {
        if (entry is null) return;
        Document.History.JumpTo(entry.Target, Document);
        AfterHistoryWalk();
    }

    private void RebuildHistoryItems()
    {
        var history = Document.History;
        HistoryItems.Clear();
        // Row 0 is the document's starting point (nothing applied).
        HistoryItems.Add(new HistoryEntryViewModel(0, "Исходное состояние")
        {
            IsCurrent = history.Cursor == 0,
        });
        for (int i = 0; i < history.Commands.Count; i++)
        {
            int target = i + 1; // this command applied
            HistoryItems.Add(new HistoryEntryViewModel(target, LocalizeCommand(history.Commands[i].DisplayName))
            {
                IsCurrent = target == history.Cursor,
                IsFuture = target > history.Cursor,
            });
        }
    }

    /// <summary>Map a command's English DisplayName to a Russian label for the UI.</summary>
    private static string LocalizeCommand(string displayName) => displayName switch
    {
        "Draw stroke"     => "Штрих",
        "Fill"            => "Заливка",
        "Clear canvas"    => "Очистка холста",
        "Resize canvas"   => "Изменение размера",
        "Paste"           => "Вставка",
        "Crop"            => "Кадрирование",
        "Erase selection" => "Удаление выделения",
        "Layer properties" => "Свойства слоя",
        "Открытие"        => "Открытие",
        "Rotate CW"       => "Поворот по часовой",
        "Rotate CCW"      => "Поворот против часовой",
        "Flip horizontal" => "Отражение по горизонтали",
        "Flip vertical"   => "Отражение по вертикали",
        _                 => displayName,
    };

    // ───────── File ─────────
    /// <summary>
    /// Спросить про несохранённую работу перед тем, как заменить документ.
    /// False - пользователь передумал. «Создать» и «Открыть» затирают холст целиком, и до
    /// этого вопроса единственным предупреждением был вопрос при закрытии окна, которого
    /// после замены документа уже не будет: метка сохранения сдвигается.
    /// </summary>
    private bool ConfirmDiscard(string action)
    {
        if (!IsDirty) return true;
        var answer = MessageBox.Show(
            $"Рисунок изменён. Сохранить перед тем, как {action}?",
            "Paint Pro", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return TrySaveForClose();
        return true;
    }

    [RelayCommand] private void NewDocument()
    {
        if (!ConfirmDiscard("создать новый")) return;
        var cmd = new ClearCanvasCommand();
        Document.History.ExecuteAndPush(cmd, Document);
        // Новый документ - это уже не тот файл. Без отвязки Ctrl+S уходил в
        // SaveOrSaveAs с прежним LastSavedPath/LastOpenedPath и молча
        // перезаписывал ранее открытую картинку чистым холстом.
        FileService.Detach();
        Document.History.MarkSaved();
        InvalidateCanvas?.Invoke();
    }

    [RelayCommand]
    private void Open()
    {
        if (!ConfirmDiscard("открыть другой файл")) return;
        ReportOpen(FileService.OpenImageDialog());
    }

    /// <summary>Открыть файл по пути - drag &amp; drop.</summary>
    public void OpenPath(string path)
    {
        if (!ConfirmDiscard("открыть другой файл")) return;
        ReportOpen(FileService.OpenImage(path));
    }

    private void ReportOpen(OpenOutcome outcome)
    {
        if (outcome.Status == OpenStatus.Failed)
        {
            MessageBox.Show($"Не удалось открыть файл.\n{outcome.Error}",
                "Ошибка открытия", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (outcome.Bitmap is not { } bmp) return;
        // Команда рисует картинку в свои битмапы, оригинал ей после этого не нужен.
        using (bmp) ApplyOpenedBitmap(bmp);
    }

    public void ApplyOpenedBitmap(SKBitmap bmp)
    {
        Document.CommitFloating();
        Document.Selection = null;

        // Открытый файл заменяет документ целиком. Прежняя версия меняла размер холста
        // и рисовала картинку только в активный слой: ResizeCanvasCommand переносит
        // содержимое всех слоёв, поэтому старый рисунок с верхних слоёв оставался
        // лежать поверх открытой фотографии, и следующий Ctrl+S записывал её вместе
        // с ним - Flatten складывает все слои.
        Document.History.ExecuteAndPush(DocumentTransform.OpenImage(Document, bmp), Document);
        Document.ActiveLayerIndex = 0;
        // Только что открытый файл - это не несохранённая работа. Без сдвига метки
        // окно сразу после «Открыть» спрашивало про сохранение и по «Да» переписывало
        // файл тем же содержимым.
        Document.History.MarkSaved();
        InvalidateCanvas?.Invoke();
    }

    /// <summary>
    /// Версия приложения для статусбара и «О программе». Берётся из атрибутов сборки,
    /// то есть из &lt;Version&gt; в csproj - единственного места, где она задана. Раньше
    /// «Paint Pro 1.0» было вписано строкой в XAML и в обработчик «О программе», и к
    /// 1.6.0 оба места отстали на шесть релизов.
    /// </summary>
    public static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// Подпись «Paint Pro 1.6.0» для статусбара. Свойство экземпляра, а не статическое:
    /// WPF резолвит путь привязки по экземпляру DataContext и статические свойства так не
    /// находит - привязка молча осталась бы пустой.
    /// </summary>
    public string VersionLabel => $"Paint Pro {AppVersion}";

    /// <summary>
    /// True if there is work that a save would capture and closing would lose. Метку
    /// «здесь сохранено» ведёт сам <see cref="HistoryManager"/>: он же выбрасывает
    /// старые записи при переполнении и сдвигает вместе с ними курсор, а копия метки
    /// снаружи об этом не узнавала и после тысячи правок объявляла документ чистым.
    /// </summary>
    public bool IsDirty
        => Document.History.IsDirtySinceSave || Document.FloatingPickup is not null;

    [RelayCommand] private void Save()
    {
        Document.CommitFloating();
        Report(FileService.SaveOrSaveAs(Document));
    }

    [RelayCommand] private void SaveAs()
    {
        Document.CommitFloating();
        Report(FileService.SaveAsDialog(Document));
    }

    /// <summary>Save for the close-confirmation flow. False if the user backed out of the dialog.</summary>
    public bool TrySaveForClose()
    {
        Document.CommitFloating();
        var outcome = FileService.SaveOrSaveAs(Document);
        Report(outcome);
        return outcome.Status is SaveStatus.Ok or SaveStatus.FormatChanged;
    }

    private void Report(SaveOutcome outcome)
    {
        if (outcome.Status is SaveStatus.Ok or SaveStatus.FormatChanged)
            Document.History.MarkSaved();

        if (outcome.Status == SaveStatus.FormatChanged)
        {
            MessageBox.Show(
                $"Этот формат записывать нельзя, файл сохранён как PNG:\n{outcome.Path}",
                "Формат заменён", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (outcome.Status == SaveStatus.Failed)
        {
            MessageBox.Show($"Не удалось сохранить файл.\n{outcome.Error}",
                "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ───────── Clipboard ─────────
    [RelayCommand] private void CopySelection() => ClipboardService.Copy(Document);
    [RelayCommand] private void CutSelection()
    {
        // Поднятое выделение обнуляет Selection, поэтому проверка на неё одна
        // отправляла Ctrl+X по перетащенному объекту в никуда. Здесь нужен не
        // EraseSelection, а DiscardFloating: он оставляет дыру и пишет её в
        // историю, тогда как CancelFloating вернул бы пиксели на место.
        if (Document.FloatingPickup is not null)
        {
            ClipboardService.Copy(Document);
            Document.DiscardFloating();
            InvalidateCanvas?.Invoke();
            return;
        }
        if (Document.Selection is null) return;
        ClipboardService.Copy(Document);
        EraseSelection();
    }

    /// <summary>Erase the current selection through history so it can be undone. Clears the selection.</summary>
    private void EraseSelection()
    {
        var cmd = BuildEraseCommand();
        if (cmd is null) return;
        Document.History.ExecuteAndPush(cmd, Document);
        Document.Selection = null;
        InvalidateCanvas?.Invoke();
    }

    /// <summary>Translate the active selection into an undoable <see cref="EraseRegionCommand"/>, or null if there's nothing to erase.</summary>
    private EraseRegionCommand? BuildEraseCommand()
    {
        if (Document.ActiveLayer is not PixelLayer pl) return null;
        var canvasRect = new SKRectI(0, 0, pl.Width, pl.Height);
        var fill = PickupOps.EraseColor(Document, pl);
        switch (Document.Selection)
        {
            case RectSelection rs:
            {
                var b = SKRectI.Intersect(SKRectI.Round(rs.Rect), canvasRect);
                return b.IsEmpty ? null : new EraseRegionCommand(b, null, fill);
            }
            case PolygonSelection ps:
            {
                var b = SKRectI.Intersect(SKRectI.Round(ps.BoundingBox), canvasRect);
                return b.IsEmpty ? null : new EraseRegionCommand(b, ps.Corners.ToArray(), fill);
            }
            default:
                return null;
        }
    }
    [RelayCommand] private void Paste()
    {
        var bmp = ClipboardService.TryGetImage();
        if (bmp is null) return;
        // Инструмент переключаем до вставки, а не после. Смена инструмента зовёт
        // OnDeactivate у прежнего, а QuadTool и SelectTool делают там CommitFloating:
        // вставка с активным «Четырёхугольником» прижималась к холсту в точке (20, 20)
        // раньше, чем пользователь успевал её увидеть, и подвинуть было уже нечего.
        ActiveTool = ToolKind.Select;
        var cmd = new PasteCommand(bmp, new SKPoint(20, 20));
        Document.History.ExecuteAndPush(cmd, Document);
        bmp.Dispose();
        InvalidateCanvas?.Invoke();
    }

    // ───────── Selection / floating ─────────
    [RelayCommand] private void SelectAll()
    {
        Document.Selection = new RectSelection(0, 0, Document.CanvasWidth, Document.CanvasHeight);
        ActiveTool = ToolKind.Select;
    }
    [RelayCommand] private void Deselect() { Document.Selection = null; }
    [RelayCommand] private void DeleteSelection()
    {
        if (Document.FloatingPickup is not null)
        {
            // Drop the lifted pixels and keep the hole, recorded so it can be undone.
            Document.DiscardFloating();
            InvalidateCanvas?.Invoke();
            return;
        }
        if (Document.Selection is null) return;
        EraseSelection(); // undoable erase of the selected region
    }
    [RelayCommand] private void CommitFloating()
    {
        Document.CommitFloating();
        InvalidateCanvas?.Invoke();
    }
    [RelayCommand] private void CancelFloating()
    {
        if (Document.FloatingPickup is null) { Document.Selection = null; return; }
        // Escape must leave no trace: the lifted pixels go back where they came from.
        Document.CancelFloating();
        InvalidateCanvas?.Invoke();
    }

    // ───────── Image ops ─────────
    [RelayCommand] private void Rotate90()        => RotateActiveLayer(MathF.PI / 2f);
    [RelayCommand] private void RotateMinus90()   => RotateActiveLayer(-MathF.PI / 2f);
    [RelayCommand] private void FlipHorizontal()  => FlipActiveLayer(horizontal: true);
    [RelayCommand] private void FlipVertical()    => FlipActiveLayer(horizontal: false);

    private void RotateActiveLayer(float radians)
    {
        Document.CommitFloating();
        Document.History.ExecuteAndPush(DocumentTransform.Rotate(Document, radians), Document);
        InvalidateCanvas?.Invoke();
    }

    private void FlipActiveLayer(bool horizontal)
    {
        Document.CommitFloating();
        Document.History.ExecuteAndPush(DocumentTransform.Flip(Document, horizontal), Document);
        InvalidateCanvas?.Invoke();
    }

    [RelayCommand] private void ResizeCanvas()
    {
        var (w, h) = (Document.CanvasWidth, Document.CanvasHeight);
        var input = Views.PromptDialog.Show(
            "Новый размер холста в формате ШxВ (например, 1200x800):",
            "Изменить размер холста",
            $"{w}x{h}");
        if (string.IsNullOrWhiteSpace(input)) return;
        var parts = input.Split('x', 'X', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return;
        if (!int.TryParse(parts[0], out var nw) || !int.TryParse(parts[1], out var nh)) return;
        if (!Commands.ResizeCanvasCommand.IsAllowed(nw, nh))
        {
            MessageBox.Show(
                $"Размер должен быть от 1 до {Commands.ResizeCanvasCommand.MaxDimension} по каждой стороне " +
                $"и не больше {Commands.ResizeCanvasCommand.MaxPixels / 1_000_000} млн пикселей всего.",
                "Слишком большой холст", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Document.CommitFloating();
        var cmd = new ResizeCanvasCommand(nw, nh, SKColors.White);
        Document.History.ExecuteAndPush(cmd, Document);
        InvalidateCanvas?.Invoke();
    }

    /// <summary>Called by host when the user finished typing for the TextTool prompt.</summary>
    public void ProvideText(string text) => _textTool.CommitText(text);
}
