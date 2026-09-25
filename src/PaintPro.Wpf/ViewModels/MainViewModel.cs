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
    private readonly CropTool _cropTool = new();

    /// <summary>Рамка кадрирования обведена и ждёт «Обрезать» или «Отмена».</summary>
    [ObservableProperty] private bool _cropPending;

    [RelayCommand] private void ApplyCrop() { _cropTool.Apply(ToolContext); InvalidateCanvas?.Invoke(); }

    [RelayCommand] private void CancelCrop() { _cropTool.Cancel(ToolContext); InvalidateCanvas?.Invoke(); }

    /// <summary>Raised when canvas needs an immediate visual refresh (rare; usually PropertyChanged handles it).</summary>
    public event Action? InvalidateCanvas;
    /// <summary>Raised when the TextTool wants a string from the user at <see cref="SKPoint"/>.</summary>
    public event Action<SKPoint>? TextRequested;

    /// <summary>VM-wrapped layer rows; rebuilt whenever Document.Layers changes.</summary>
    public ObservableCollection<LayerListItemViewModel> LayerItems { get; } = new();

    /// <summary>
    /// Те же строки для панели - верхний слой первым, как в Electron-версии и в любом
    /// редакторе. <see cref="LayerItems"/> остаётся в порядке стопки: по нему ходят команды
    /// и тесты.
    /// </summary>
    public ObservableCollection<LayerListItemViewModel> LayerItemsTopFirst { get; } = new();

    /// <summary>Размер выделения или поднятого объекта для строки состояния; пусто - нет ни того, ни другого.</summary>
    [ObservableProperty] private string _selectionSizeLabel = "";

    public MainViewModel()
    {
        Document = new Document(Document.DefaultWidth, Document.DefaultHeight);
        ToolContext = new ToolContext(Document)
        {
            PrimaryColor = SKColors.Black,
            ToolSize = 4f,
            Opacity = 1f,
            ReportHint = ShowHint,
        };
        _pickerTool.ColorPicked += c => PrimaryColor = c;
        _textTool.TextRequested += p => TextRequested?.Invoke(p);
        _cropTool.PendingChanged += () => CropPending = _cropTool.HasPending;

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
            [ToolKind.RightTriangle] = new RightTriangleShapeTool(),
            [ToolKind.Star]    = new StarShapeTool(),
            [ToolKind.Arrow]   = new ArrowShapeTool(),
            [ToolKind.Heart]   = new HeartShapeTool(),
            [ToolKind.Select]  = new SelectTool(),
            [ToolKind.Quad]    = new QuadTool(),
            [ToolKind.Crop]    = _cropTool,
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
        Document.History.Changed += SyncHistoryItems;
        SyncHistoryItems();
        Document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Document.ActiveLayerIndex)) RebuildLayerItems();
            // Selection/floating changes trigger overlay redraws; the CanvasView listens for these.
            if (e.PropertyName is nameof(Document.Selection) or nameof(Document.FloatingPickup)
                or nameof(Document.CanvasWidth) or nameof(Document.CanvasHeight))
            {
                InvalidateCanvas?.Invoke();
                UpdateSelectionSize();
                if (e.PropertyName is nameof(Document.CanvasWidth) or nameof(Document.CanvasHeight)) SyncCanvasSizeText();
            }
            // Холст стал больше - прежний масштаб может оказаться неподъёмным. Открытие
            // фотографии в документ, увеличенный до восьмикратного, просило три гигабайта
            // под экранную поверхность и роняло приложение до того, как пользователь
            // успевал что-либо нажать.
            if (e.PropertyName is nameof(Document.CanvasWidth) or nameof(Document.CanvasHeight))
            {
                var capped = ClampZoom(Zoom);
                if (Math.Abs(capped - Zoom) > 1e-9) Zoom = capped;
            }
            // Подъём и снятие пикапа меняют ответ CanUndo, а истории об этом знать неоткуда.
            if (e.PropertyName == nameof(Document.FloatingPickup)) UndoCommand.NotifyCanExecuteChanged();
        };
    }

    // ───────── Подсказка в статусбаре ─────────

    /// <summary>
    /// Короткое объяснение, почему жест ничего не сделал. Пустая строка - строки нет.
    /// Модальное окно на каждый штрих по скрытому слою было бы хуже самой ошибки.
    /// </summary>
    [ObservableProperty] private string _statusHint = "";

    private System.Windows.Threading.DispatcherTimer? _hintTimer;

    /// <summary>Показать подсказку и убрать её через несколько секунд.</summary>
    private void ShowHint(string text)
    {
        StatusHint = text;
        _hintTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        _hintTimer.Tick -= ClearHint;
        _hintTimer.Tick += ClearHint;
        // Перезапуск, а не продление: повторный тот же жест должен обновлять отсчёт.
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void ClearHint(object? sender, EventArgs e)
    {
        _hintTimer?.Stop();
        StatusHint = "";
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
        LayerItemsTopFirst.Clear();
        for (int i = LayerItems.Count - 1; i >= 0; i--) LayerItemsTopFirst.Add(LayerItems[i]);
    }

    /// <summary>
    /// Слой на ступень выше или ниже в стопке (1.28.0, как «▲ ▼» Electron-версии). Бумага
    /// всегда внизу; отказ называет причину - их две, и они разные.
    /// </summary>
    public bool MoveLayer(int index, int delta)
    {
        int to = index + delta;
        if (index <= 0 || to <= 0 || index >= Document.Layers.Count || to >= Document.Layers.Count)
        {
            ShowHint(index == 0 || to == 0
                ? "Бумага документа всегда лежит снизу."
                : "Слой уже с краю стопки: двигать его дальше некуда.");
            return false;
        }
        Document.CommitFloating();
        Document.History.ExecuteAndPush(new Commands.LayerMoveCommand(index, to), Document);
        return true;
    }

    [RelayCommand] private void MoveLayerUp(LayerListItemViewModel? item)
    {
        if (item is not null) MoveLayer(Document.Layers.IndexOf(item.Layer), +1);
    }

    [RelayCommand] private void MoveLayerDown(LayerListItemViewModel? item)
    {
        if (item is not null) MoveLayer(Document.Layers.IndexOf(item.Layer), -1);
    }

    /// <summary>«Очистить холст» с вопросом, как в Electron-версии.</summary>
    [RelayCommand] private void ClearCanvas()
    {
        if (Views.GlassMessage.Show("Очистить холст? Все слои станут пустыми, бумага - белой. Отменить можно Ctrl+Z.",
                "Очистка холста", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes) return;
        ClearCanvasNow();
    }

    /// <summary>Сама очистка, без вопроса: её зовут тесты.</summary>
    public void ClearCanvasNow()
    {
        Document.CommitFloating();
        Document.Selection = null;
        Document.History.ExecuteAndPush(Commands.DocumentTransform.Clear(Document), Document);
        InvalidateCanvas?.Invoke();
    }

    private void UpdateSelectionSize()
    {
        SKRect? box = Document.FloatingPickup is { } f ? SKRect.Create(f.X, f.Y, f.Width, f.Height) : Document.Selection?.BoundingBox;
        SelectionSizeLabel = box is { } b && b.Width >= 1 && b.Height >= 1
            ? $"Выделение: {(int)MathF.Round(b.Width)} × {(int)MathF.Round(b.Height)}"
            : "";
    }

    /// <summary>Когда и по какому слою последний раз двигали ползунок прозрачности.</summary>
    private DateTime _lastOpacityEdit = DateTime.MinValue;
    private Guid _lastOpacityLayer;

    /// <summary>
    /// Насколько долго правки прозрачности считаются одним жестом. Ползунок шлёт значение
    /// на каждый пиксель перетаскивания, паузы между ними миллисекундные; отдельный заход
    /// к тому же слою через секунду - уже другая правка и заслуживает своей записи.
    /// </summary>
    private static readonly TimeSpan OpacityGesture = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// Правка видимости/прозрачности слоя из панели - через историю.
    ///
    /// Перетаскивание ползунка склеивается в одну запись, иначе один жест оставил бы
    /// полсотни. Флажок видимости - отдельное нажатие и всегда отдельная запись: склейка
    /// по одному только «последняя запись про этот слой» объединяла и правки, сделанные
    /// с разницей в час, и один Ctrl+Z откатывал обе.
    /// </summary>
    private void ApplyLayerProperties(Layer layer, bool visible, float opacity)
    {
        if (layer.Visible == visible && Math.Abs(layer.Opacity - opacity) < 0.0001f) return;

        var now = DateTime.UtcNow;
        bool sameGesture = layer.Visible == visible          // видимость не трогали: это ползунок
            && _lastOpacityLayer == layer.Id
            && now - _lastOpacityEdit < OpacityGesture;

        if (sameGesture && Document.History.Current is LayerPropertyCommand last && last.LayerId == layer.Id)
        {
            last.MergeInto(Document, visible, opacity);
            // Курсор при склейке не двигается, и без этого признак несохранённой работы
            // о правке не узнавал: изменение доходило до файла, а вопрос при закрытии - нет.
            Document.History.AmendCurrent();
        }
        else
        {
            var cmd = new LayerPropertyCommand(layer, visible, opacity);
            if (!cmd.ChangedAnything) return;
            Document.History.ExecuteAndPush(cmd, Document);
        }
        _lastOpacityEdit = now;
        _lastOpacityLayer = layer.Id;
        InvalidateCanvas?.Invoke();
    }

    [RelayCommand]
    private void AddLayer()
    {
        // Каждый слой - это целый холст в памяти, и складываются они без всякого предела.
        // На большом документе «Добавить слой» отъедало по полсотни мегабайт за нажатие;
        // потолок стоял на площади ОДНОГО холста и не стоял на их сумме.
        int max = LayerStackCommand.MaxLayersFor(Document.CanvasWidth, Document.CanvasHeight);
        if (Document.Layers.Count >= max)
        {
            ShowHint(max <= 1
                ? $"Холст {Document.CanvasWidth}×{Document.CanvasHeight} слишком велик, чтобы завести ещё один слой"
                : $"Больше {max} слоёв на холсте такого размера завести нельзя");
            return;
        }
        Document.CommitFloating();
        Document.History.ExecuteAndPush(LayerStackCommand.Add(Document, NextLayerName()), Document);
        InvalidateCanvas?.Invoke();
    }

    /// <summary>
    /// Имя, которого в стопке ещё нет.
    ///
    /// Номер брался из числа слоёв, а оно уменьшается при удалении: «добавили два, удалили
    /// первый, добавили ещё» давало второй «Layer 2». В панели две одинаковые строки,
    /// отличить их можно только положением, и пользователь выключал видимость не тому
    /// слою, которому собирался.
    /// </summary>
    private string NextLayerName()
    {
        var taken = Document.Layers.Select(l => l.Name).ToHashSet();
        for (int n = Document.Layers.Count; ; n++)
        {
            var name = $"Слой {n}"; // как в Electron-версии
            if (taken.Add(name)) return name;
        }
    }

    [RelayCommand]
    private void RemoveLayer(LayerListItemViewModel? item)
    {
        if (item is null) return;
        if (item.Layer is not PixelLayer pl || Document.Layers.IndexOf(pl) < 0) return;
        // Проверка «слой в стопке один» стояла ПЕРЕД объяснением про бумагу и молча
        // съедала единственный случай, когда она вообще срабатывает: в новом документе
        // слой ровно один, он же бумага, и нажатие на «x» рядом с ним не делало ничего и
        // не говорило ни слова. Порядок теперь обратный - причина отказа у обоих случаев
        // одна и та же и называется одинаково.
        // Нижний слой - это бумага документа, и весь остальной код исходит именно из
        // этого: ластик красит по нему белым, а не вычитает пиксели; подъём выделения
        // выкусывает из него фон; смена размера холста заливает новую площадь белым;
        // «Очистить» возвращает его к белому, а слои над ним - к прозрачному.
        //
        // Пока бумагу можно было удалить, ею молча становился следующий слой - со всеми
        // этими правилами разом. Прозрачный слой с рисунком после первой же смены
        // размера холста оказывался залит белым по всей площади, а ластик переставал
        // стирать и начинал красить. Пользователь при этом ничего такого не заказывал:
        // он удалил слой, который ему не нужен.
        if (ReferenceEquals(Document.Layers[0], pl))
        {
            ShowHint($"Слой «{pl.Name}» - бумага документа, удалить его нельзя. Очистите его или спрячьте.");
            return;
        }
        if (Document.Layers.Count <= 1)
        {
            ShowHint("В документе всего один слой - удалить его нельзя.");
            return;
        }
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

    /// <summary>
    /// Инструменты, которые умеют работать с уже поднятым объектом: «Выделение» двигает и
    /// масштабирует, «Четырёхугольник» вдобавок тянет углы маски.
    /// </summary>
    private static bool KeepsPickup(ToolKind k) => k is ToolKind.Select or ToolKind.Quad;

    partial void OnActiveToolChanged(ToolKind value)
    {
        OnPropertyChanged(nameof(ActiveToolHasSize));
        // Инструмент сбрасывает своё состояние жеста, но поднятый объект не трогает.
        ActiveToolInstance?.OnDeactivate(ToolContext);

        // Прижимать - здесь: только тут известно, на что меняют. «Выделение» и
        // «Четырёхугольник» - две руки для одного и того же объекта, и переход между ними
        // ничего не заканчивает. Пока каждый из них прижимал объект у себя в OnDeactivate,
        // нажатие Q по перетащенному выделению прибивало его к холсту раньше, чем
        // пользователь успевал взяться за угол: путь «выдели прямоугольником - искриви
        // углы» был закрыт целиком. В Electron-версии это исключение записано прямо в
        // обработчике кнопок инструментов.
        if (!KeepsPickup(value)) Document.CommitFloating();

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

    // ───────── Размер инструмента: границы, шаг, колесо ─────────

    /// <summary>Границы размера. Ползунок в разметке берёт их отсюда, а не из своих чисел:
    /// разойдись они - колесо и ползунок стали бы упираться в разные пределы.</summary>
    public const int MinToolSize = 1;
    // 300 с 1.31.0 (было 100): Алексей попросил втрое больше - широкой кистью и ластиком
    // закрашивают фон. Тот же предел в Electron-версии (size-input max), сверяет тест.
    public const int MaxToolSize = 300;

    public int ToolSizeMin => MinToolSize;
    public int ToolSizeMax => MaxToolSize;

    /// <summary>
    /// Влияет ли размер на этот инструмент.
    ///
    /// Правило одно: колесо меняет ровно то, что меняет ползунок. У заливки, пипетки,
    /// выделения, кадрирования и руки размера нет вовсе. Текст в этот список входит -
    /// в этой версии ползунок задаёт именно кегль (см. TextTool), и делать колесо
    /// исключением значило бы оставить необъяснимую дырку.
    /// </summary>
    public static bool HasToolSize(ToolKind tool) => tool switch
    {
        ToolKind.Fill or ToolKind.Picker or ToolKind.Select or
        ToolKind.Quad or ToolKind.Crop or ToolKind.Hand => false,
        _ => true,
    };

    public bool ActiveToolHasSize => HasToolSize(ActiveTool);

    /// <summary>
    /// Шаг размера за одну засечку колеса.
    ///
    /// Ровно один пиксель означал бы сотню засечек на весь ползунок - колесо было бы
    /// бесполезно. Шаг растёт вместе с размером: у тонкого карандаша важен каждый
    /// пиксель, у стопиксельной кисти - уже нет.
    /// </summary>
    public static int ToolSizeStep(int size) => Math.Max(1, (int)Math.Round(size / 10.0));

    /// <summary>
    /// Шагнуть размером вверх или вниз - то, что делает колесо над холстом.
    /// Возвращает false, если размер не изменился; про упор в предел говорит сама.
    /// </summary>
    public bool AdjustToolSize(bool up)
    {
        if (!ActiveToolHasSize) return false;

        var before = Math.Clamp(ToolSize, MinToolSize, MaxToolSize);
        var step = ToolSizeStep(before);
        var after = Math.Clamp(up ? before + step : before - step, MinToolSize, MaxToolSize);
        if (after == before)
        {
            // Молчаливый отказ читается как поломка: у предела надо сказать, что это предел.
            ShowHint(up
                ? $"Больше некуда: {MaxToolSize} px - предел размера."
                : $"Меньше некуда: {MinToolSize} px - предел размера.");
            ToolSize = after;
            return false;
        }

        ToolSize = after;
        return true;
    }

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

    /// <summary>
    /// Инструменты меряют зоны хвата в экранных пикселях, а мышь получают в координатах
    /// документа: масштаб - единственное, что связывает одно с другим, и он обязан
    /// доходить до них. Без этого зона хвата угловой точки жила в пикселях документа и
    /// «десять пикселей» означало десять экранных только при масштабе 1:1.
    /// </summary>
    partial void OnZoomChanged(double value) => ToolContext.Zoom = value;

    /// <summary>
    /// Наибольший масштаб для нынешнего холста. До 1.30.0 зависел от размера холста -
    /// экранная поверхность растрировалась целиком; теперь растр размером с окно, и потолок
    /// один на всех (<see cref="ViewGeometry.LargestAllowedZoom"/>).
    /// </summary>
    private double MaxZoom => ViewGeometry.LargestAllowedZoom(Document.CanvasWidth, Document.CanvasHeight);

    [RelayCommand] private void ZoomIn()
    {
        var next = GeometryMath.NextZoomStep((float)Zoom, true);
        // На верхней ступени следующей нет, и NextZoomStep отвечает той же. Это тоже
        // отказ: до 1.30.0 здесь молчали - подсказка была только у потолка по памяти, а
        // на маленьком холсте Ctrl+= на 800% просто ничего не делал. В Electron-версии
        // подсказка есть.
        if (next > MaxZoom + 1e-6 || next <= Zoom + 1e-6)
        {
            // Молчащий отказ неотличим от сломанной кнопки - см. остальные отказы.
            ShowHint("Больше увеличить нельзя: это предел масштаба.");
            return;
        }
        Zoom = next;
    }

    [RelayCommand] private void ZoomOut() => Zoom = GeometryMath.NextZoomStep((float)Zoom, false);

    /// <summary>
    /// «Один к одному» - если холст такой величины вообще можно показать один к одному.
    /// У документа в сто с лишним мегапикселей нельзя, и притвориться, что можно, значит
    /// уронить приложение по кнопке «1:1».
    /// </summary>
    [RelayCommand] private void ZoomReset() => Zoom = Math.Min(1.0, MaxZoom);

    /// <summary>
    /// Прижать масштаб к потолку нынешнего холста. Зовут это и смена размера холста, и
    /// открытие файла: увеличили мелкий документ до восьмикратного, открыли фотографию -
    /// и масштаб, оставшийся от прежнего документа, уже неподъёмный.
    /// </summary>
    public double ClampZoom(double value)
        => Math.Clamp(value, GeometryMath.ZoomSteps[0], MaxZoom);

    // ───────── Palette / recent colours ─────────
    public ObservableCollection<ColorEntryViewModel> Palette { get; } = new();
    public ObservableCollection<ColorEntryViewModel> RecentColors { get; } = new();

    private void InitPalette()
    {
        // Пять рядов по восемь - серые, чистые, тёмные, средние, светлые (1.28.0). Та же
        // палитра, что в Electron-версии (paint-pro.html, colors), в том же порядке. Была
        // россыпь из 43 цветов по девять в ряд с неполным последним рядом.
        var hexes = PaletteHexes;
        foreach (var h in hexes)
        {
            if (SKColor.TryParse(h, out var c)) Palette.Add(new ColorEntryViewModel(c));
        }
    }

    public static readonly string[] PaletteHexes =
    {
        "#000000","#434343","#666666","#999999","#B7B7B7","#D9D9D9","#EFEFEF","#FFFFFF",
        "#FF0000","#FF9900","#FFFF00","#00FF00","#00FFFF","#0000FF","#9900FF","#FF00FF",
        "#980000","#B45F06","#BF9000","#38761D","#134F5C","#1155CC","#351C75","#741B47",
        "#E06666","#F6B26B","#FFD966","#93C47D","#76A5AF","#6D9EEB","#8E7CC3","#C27BA0",
        "#F4CCCC","#FCE5CD","#FFF2CC","#D9EAD3","#D0E0E3","#C9DAF8","#D9D2E9","#EAD1DC",
    };

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

    /// <summary>
    /// Окно выбора цвета: квадрат оттенков, полоса тона, HEX. Раньше здесь было окно
    /// «Введите цвет в HEX» - подобрать цвет глазами было нельзя.
    /// </summary>
    [RelayCommand] private void PickCustomColor()
    {
        if (Views.ColorPickerDialog.Show(PrimaryColor) is { } c) PrimaryColor = c;
    }

    // ───────── History ─────────
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() { Document.History.Undo(Document); AfterHistoryWalk(); }

    /// <summary>
    /// Поднятое выделение - правка, которой в списке ещё нет, и первый Ctrl+Z снимает
    /// именно её (<see cref="HistoryManager.Undo"/>). Пока сюда смотрел один только
    /// курсор истории, на чистом документе кнопка была выключена и подъём нельзя было
    /// отменить ничем, кроме Escape.
    /// </summary>
    private bool CanUndo => Document.History.CanUndo || Document.FloatingPickup is not null;

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

    /// <summary>Timeline rows for the History panel; kept in step with the history by <see cref="SyncHistoryItems"/>.</summary>
    public ObservableCollection<HistoryEntryViewModel> HistoryItems { get; } = new();

    [RelayCommand]
    private void JumpToHistory(HistoryEntryViewModel? entry)
    {
        if (entry is null) return;
        Document.History.JumpTo(entry.Target, Document);
        AfterHistoryWalk();
    }

    /// <summary>
    /// Выключить правку или включить её обратно. Идущие после неё остаются на месте - в
    /// этом весь смысл, в отличие от прыжка по ленте, который откатывает всё после
    /// выбранной строки.
    ///
    /// Отказ объясняется словами: у строк без выключателя галочки в панели нет вовсе, но
    /// правило зависит от ЛЕНТЫ, а не от самой записи, - стоит дорисовать что-нибудь
    /// поверх, и вчера выключаемая правка сегодня уже нет. Между тем, как пользователь
    /// увидел галочку, и тем, как он по ней щёлкнул, лента могла измениться.
    /// </summary>
    [RelayCommand]
    private void ToggleHistoryEntry(HistoryEntryViewModel? entry)
    {
        if (entry?.Command is not { } cmd) return;
        if (!Document.History.SetEnabled(cmd, !entry.Enabled, Document))
        {
            ShowHint("Эту правку выключить нельзя: ниже неё есть та, что пишет картинку целиком");
            SyncHistoryItems();
            // Щелчок уже перевернул галочку на экране, а история отказала: сама строка не
            // поменялась, и без явного напоминания галочка так и врала бы.
            entry.ReassertEnabled();
            return;
        }
        AfterHistoryWalk();
    }

    /// <summary>Почему у строки нет выключателя - словами, для подсказки при наведении.</summary>
    private static string ExplainNoToggle(IDocumentCommand cmd, HistoryManager history)
    {
        if (cmd.WritesSnapshot)
            return "Эту правку выключить нельзя: она записывает картинку целиком, а не поверх неё";
        return "Выключить нельзя: ниже в ленте есть правка, которая записывает картинку целиком";
    }

    private const string ToggleHintText = "Выключить эту правку, не трогая те, что идут после неё";

    /// <summary>
    /// Привести строки ленты в соответствие с историей - на месте, а не заново.
    ///
    /// До 1.34.0 здесь было «стереть всё и создать заново» на каждую правку. Строка ленты -
    /// это флажок, кнопка и подсказка, а список не был виртуальным, так что создавалась и
    /// раскладывалась каждая из них, даже невидимая. На ста тридцати правках это 10 мс на
    /// строки и 58 мс на раскладку после КАЖДОГО мазка, и пауза росла с каждым следующим:
    /// быстрые мазки подряд подвисали. Теперь строка правится, только если у неё что-то
    /// поменялось: новый мазок - одна новая строка и две сменённые пометки.
    /// </summary>
    private void SyncHistoryItems()
    {
        var history = Document.History;
        var commands = history.Commands;
        int n = commands.Count;

        // Row 0 is the document's starting point (nothing applied) — пока лента не
        // переполнялась. Стоит ей выбросить самые старые записи, и нулевая позиция
        // означает уже не чистый лист, а состояние после забытых правок: строка обещала
        // вернуть документ к началу работы, а возвращала к середине.
        if (HistoryItems.Count == 0 || HistoryItems[0].Command is not null)
            HistoryItems.Insert(0, new HistoryEntryViewModel(0, ""));
        var start = HistoryItems[0];
        start.Label = history.Trimmed ? "Дальше отмена не идёт" : "Исходное состояние";
        start.IsCurrent = history.Cursor == 0;

        // Лента выбросила самые старые записи: их строки убираем разом, остальные только
        // сдвигаются. Иначе на пределе глубины каждая правка сдвигала бы все строки и
        // пересоздавала их одну за другой.
        if (n > 0)
        {
            int at = -1;
            for (int r = 1; r < HistoryItems.Count; r++)
                if (ReferenceEquals(HistoryItems[r].Command, commands[0])) { at = r; break; }
            for (int r = at - 1; r >= 1; r--) HistoryItems.RemoveAt(r);
        }

        // Выключатель есть, пока ниже (позже) нет записи, пишущей картинку целиком, - то же
        // правило, что HistoryManager.CanToggle, но одним проходом с конца, а не по проходу
        // на строку.
        var snapshotAfter = new bool[n];
        bool seen = false;
        for (int i = n - 1; i >= 0; i--)
        {
            snapshotAfter[i] = seen;
            if (commands[i].WritesSnapshot) seen = true;
        }

        for (int i = 0; i < n; i++)
        {
            int target = i + 1; // this command applied
            var cmd = commands[i];
            bool canToggle = !cmd.WritesSnapshot && !snapshotAfter[i];
            string hint = canToggle ? ToggleHintText : ExplainNoToggle(cmd, history);
            HistoryEntryViewModel row;
            if (target < HistoryItems.Count && ReferenceEquals(HistoryItems[target].Command, cmd))
            {
                row = HistoryItems[target];
            }
            else
            {
                row = new HistoryEntryViewModel(target, LocalizeCommand(cmd.DisplayName), cmd,
                                                history.IsEnabled(cmd), canToggle, hint);
                if (target < HistoryItems.Count) HistoryItems[target] = row;
                else HistoryItems.Add(row);
            }
            row.Target = target;
            row.Enabled = history.IsEnabled(cmd);
            row.CanToggle = canToggle;
            row.ToggleHint = hint;
            row.IsCurrent = target == history.Cursor;
            row.IsFuture = target > history.Cursor;
        }
        while (HistoryItems.Count > n + 1) HistoryItems.RemoveAt(HistoryItems.Count - 1);
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
        // Добавление и удаление слоя стояли в панели истории по-английски: перевода для
        // них тут просто не было, а запасной вариант отдаёт имя команды как есть.
        "Add layer"       => "Добавление слоя",
        "Remove layer"    => "Удаление слоя",
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
        var answer = Views.GlassMessage.Show(
            $"Рисунок изменён. Сохранить перед тем, как {action}?",
            "Paint Pro", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return TrySaveForClose();
        return true;
    }

    [RelayCommand] private void NewDocument()
    {
        if (!ConfirmDiscard("создать новый")) return;
        ResetDocument();
    }

    /// <summary>
    /// Собственно «Создать», без вопроса про несохранённую работу. Отдельно от <see
    /// cref="NewDocument"/>, потому что тот открывает модальное окно и в тестах его не
    /// позвать - тем же приёмом вынесен разбор размера холста
    /// (<see cref="TryParseCanvasSize"/>).
    /// </summary>
    public void ResetDocument()
    {
        // Прижимаем поднятый объект ДО очистки - как это делают поворот, отражение,
        // кадрирование и смена размера. Сама команда его только снимает, и вставленная
        // картинка, которую пользователь ещё держал в руках, исчезала бесследно: отмена
        // «Создать» возвращала слои, но не её, а запись «Вставка» оставалась в ленте
        // применённой. Прижатая, она вернётся вместе со всем остальным.
        Document.CommitFloating();
        var cmd = new ClearCanvasCommand();
        Document.History.ExecuteAndPush(cmd, Document);
        // Новый документ - это уже не тот файл. Без отвязки Ctrl+S уходил в
        // SaveOrSaveAs с прежним LastSavedPath/LastOpenedPath и молча
        // перезаписывал ранее открытую картинку чистым холстом.
        FileService.Detach();
        Document.History.MarkSaved();
        // Строки панели слоёв держат свои копии видимости и прозрачности, а «Создать»
        // возвращает бумагу к видимой и непрозрачной. Пока строки не пересобирались,
        // документ с ОДНИМ слоем (коллекция не менялась, и событие о ней не приходило)
        // оставлял в панели прежний флажок: галочка снята, а холст рисуется - и следующий
        // клик по ней прятал слой, который пользователь считал уже спрятанным. Ровно то же
        // делает после прогулки по ленте AfterHistoryWalk.
        AfterHistoryWalk();
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
            Views.GlassMessage.Show($"Не удалось открыть файл.\n{outcome.Error}",
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
    ///
    /// Сам по себе поднятый пикап правкой не считается: клик внутрь рамки поднимает
    /// пиксели, но холста не трогает, пока их не сдвинули - ровно об этом говорит
    /// <see cref="FloatingPickup.HasMoved"/>, по нему же решает, писать ли в историю,
    /// <see cref="Document.CommitFloating"/>. Пока здесь стоял признак «исходную область
    /// стёрли», нажатие на ручку без перетаскивания делало документ изменённым: стирание
    /// шло по нажатию, а сдвинуть пикап пользователь не успевал.
    /// </summary>
    public bool IsDirty
        => Document.History.IsDirtySinceSave
           || Document.FloatingPickup is { HasMoved: true };

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
            Views.GlassMessage.Show(
                $"Этот формат записывать нельзя, файл сохранён как PNG:\n{outcome.Path}",
                "Формат заменён", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (outcome.Status == SaveStatus.Failed)
        {
            Views.GlassMessage.Show($"Не удалось сохранить файл.\n{outcome.Error}",
                "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ───────── Clipboard ─────────
    [RelayCommand] private void CopySelection() => CopyToClipboard();

    /// <summary>
    /// Копирование с сообщением об отказе: молчащий Ctrl+C неотличим от сработавшего.
    ///
    /// Причин отказа две, и они разные. «Занят» - дело житейское, повторить через секунду
    /// обычно получается. «Нечего копировать» - это рамка или объект, уехавшие за край
    /// холста; раньше в буфер в таком случае уходил прозрачный пиксель 1x1, объявляя
    /// копирование удавшимся, а Ctrl+X по этому «успеху» ещё и выбрасывал сам объект.
    /// </summary>
    private bool CopyToClipboard()
    {
        switch (ClipboardService.Copy(Document))
        {
            case CopyStatus.Ok:
                return true;
            case CopyStatus.Nothing:
                ShowHint("Копировать нечего — выделенное целиком за пределами холста");
                return false;
            default:
                ShowHint("Буфер обмена занят другим приложением — копирование не удалось");
                return false;
        }
    }

    [RelayCommand] private void CutSelection()
    {
        // Поднятое выделение обнуляет Selection, поэтому проверка на неё одна
        // отправляла Ctrl+X по перетащенному объекту в никуда. Здесь нужен не
        // EraseSelection, а DiscardFloating: он оставляет дыру и пишет её в
        // историю, тогда как CancelFloating вернул бы пиксели на место.
        if (Document.FloatingPickup is not null)
        {
            // Не вырезаем, если копия не удалась: иначе пиксели пропадут в никуда.
            if (!CopyToClipboard()) return;
            if (UndoOwnedPickup()) return;
            Document.DiscardFloating();
            InvalidateCanvas?.Invoke();
            return;
        }
        if (Document.Selection is null)
        {
            // Ctrl+C без выделения копирует весь холст, а Ctrl+X - не делал ничего и
            // молчал: одна и та же пара клавиш на одном и том же документе отвечала
            // по-разному, и понять, почему, было нельзя. Вырезать холст целиком нельзя -
            // после этого не осталось бы документа, - значит надо сказать словами.
            ShowHint("Вырезать нечего — сначала выделите область");
            return;
        }
        // Команду строим до копирования: по скрытому слою стирать нечего, и класть при
        // этом пиксели в буфер обмена значило бы соврать, что вырезание состоялось.
        var cut = BuildEraseCommand();
        if (cut is null) return;
        if (!CopyToClipboard()) return;
        Apply(cut);
    }

    /// <summary>Erase the current selection through history so it can be undone. Clears the selection.</summary>
    private void EraseSelection()
    {
        if (BuildEraseCommand() is { } cmd) Apply(cmd);
    }

    /// <summary>
    /// Выполнить стирание и записать его, если оно хоть что-то изменило.
    ///
    /// Не <see cref="HistoryManager.ExecuteAndPush"/>: Delete по области, где стирать
    /// нечего - по нетронутой бумаге, по пустому месту верхнего слоя, - не менял ни
    /// одного пикселя, но оставлял запись в ленте и объявлял документ изменённым.
    /// Дальше приложение спрашивало про сохранение после жеста, от которого на холсте
    /// не осталось ничего. Тем же правилом отсеивают пустую работу заливка
    /// (<see cref="FillCommand.ChangedAnything"/>), штрих, фигура, текст и прижатие.
    /// </summary>
    private void Apply(EraseRegionCommand cmd)
    {
        cmd.Execute(Document);
        if (cmd.ChangedAnything) Document.History.Push(cmd);
        else cmd.Dispose();
        Document.Selection = null;
        InvalidateCanvas?.Invoke();
    }

    /// <summary>
    /// Translate the active selection into an undoable <see cref="EraseRegionCommand"/>, or null if there's nothing to erase.
    ///
    /// Через <see cref="ToolContext.DrawTarget"/>, как кисти, фигуры, заливка и текст:
    /// стирание по скрытому слою уходило в его битмап целиком - на экране не менялось
    /// ничего, зато в истории появлялась запись, документ считался изменённым, а Ctrl+X
    /// ещё и клал в буфер обмена картинку, из которой ничего не вырезано.
    /// </summary>
    private EraseRegionCommand? BuildEraseCommand()
    {
        if (ToolContext.DrawTarget() is not { } pl) return null;
        var canvasRect = new SKRectI(0, 0, pl.Width, pl.Height);
        var fill = PickupOps.EraseColor(Document, pl);
        switch (Document.Selection)
        {
            case RectSelection rs:
            {
                var b = SKRectI.Intersect(SKRectI.Round(rs.Rect), canvasRect);
                return b.HasArea() ? new EraseRegionCommand(b, null, fill) : null;
            }
            case PolygonSelection ps:
            {
                var b = SKRectI.Intersect(SKRectI.Round(ps.BoundingBox), canvasRect);
                return b.HasArea() ? new EraseRegionCommand(b, ps.Corners.ToArray(), fill) : null;
            }
            default:
                return null;
        }
    }
    [RelayCommand] private void Paste() => ApplyClipboard(ClipboardService.TryGetImage());

    /// <summary>
    /// Разобрать ответ буфера обмена. Отдельно от чтения самого буфера: читать его в
    /// тестах нельзя, а решать, что показать пользователю, - нужно.
    ///
    /// Пустой буфер объясняется наравне с занятым. Пока про него молчали, Ctrl+V по
    /// буферу без картинки не делал ровно ничего и ничего не говорил: отличить это от
    /// сломанной вставки было нельзя, и пользователь жал ещё и ещё. Причина при этом
    /// обычная - в буфере лежит текст или файл, а не картинка.
    /// </summary>
    public void ApplyClipboard(ClipboardOutcome outcome)
    {
        if (outcome.Status == ClipboardStatus.Busy)
        {
            ShowHint("Буфер обмена занят другим приложением — вставка не удалась");
            return;
        }
        if (outcome.Status == ClipboardStatus.Empty || outcome.Bitmap is null)
        {
            ShowHint("В буфере обмена нет картинки — вставлять нечего");
            return;
        }
        using (outcome.Bitmap) PasteBitmap(outcome.Bitmap);
    }

    /// <summary>
    /// Положить картинку на холст поднятым объектом. Отдельно от чтения буфера обмена:
    /// вставке всё равно, откуда взялись пиксели, а буфер обмена в проверке не нужен.
    /// </summary>
    public void PasteBitmap(SKBitmap bmp)
    {
        // Скрытый слой отсеиваем до вставки, как кисти, фигуры, заливка, текст и подъём
        // выделения. Плавающий объект рисуется поверх документа независимо от видимости
        // своего слоя - иначе спряталось бы то, что пользователь держит в руках, - и
        // вставленная картинка была видна ровно до прижатия: дальше она уходила в битмап
        // скрытого слоя и пропадала с экрана, оставив в истории запись. Причину отказа
        // объяснит сам DrawTarget.
        if (ToolContext.DrawTarget() is null) return;
        // Инструмент переключаем до вставки, а не после: смена инструмента на нерисующий
        // прижимает поднятое (см. OnActiveToolChanged), и вставка с активной кистью
        // прибивалась бы к холсту в точке (20, 20) раньше, чем пользователь успевал её
        // увидеть. Прежний объект, если он был, прижмёт сама PasteCommand.
        ActiveTool = ToolKind.Select;
        var cmd = new PasteCommand(bmp, PasteOrigin(
            Document.CanvasWidth, Document.CanvasHeight, bmp.Width, bmp.Height));
        Document.History.ExecuteAndPush(cmd, Document);
        InvalidateCanvas?.Invoke();
    }

    /// <summary>Отступ, с которым вставленная картинка ложится на холст.</summary>
    private const int PasteInset = 20;

    /// <summary>
    /// Левый верхний угол вставки. Обычно это отступ в двадцать пикселей от края - так
    /// видно, что картинка лежит поверх, а не приклеена к углу.
    ///
    /// Но отступ не должен уносить картинку с холста. На холсте меньше двадцати пикселей
    /// (а такой получается после кадрирования до мелочи или смены размера) вставка
    /// оказывалась ЦЕЛИКОМ за краем: Ctrl+V не показывал ничего, в ленте появлялась
    /// строка «Вставка», а первое же прижатие вычёркивало её обратно - картинку прижимать
    /// было некуда. Со стороны это выглядело как «вставка не работает».
    /// </summary>
    public static SKPoint PasteOrigin(int canvasW, int canvasH, int imageW, int imageH)
        => new(Math.Max(0, Math.Min(PasteInset, canvasW - imageW)),
               Math.Max(0, Math.Min(PasteInset, canvasH - imageH)));

    // ───────── Selection / floating ─────────
    [RelayCommand] private void SelectAll()
    {
        Document.Selection = new RectSelection(0, 0, Document.CanvasWidth, Document.CanvasHeight);
        ActiveTool = ToolKind.Select;
    }
    /// <summary>
    /// Снять выделение. Поднятый объект при этом ложится на холст: выделения у него нет -
    /// подъём его забирает, - и проверка на одну только рамку отправляла Ctrl+D в никуда.
    /// Пользователь просил закончить с выделенным, а объект оставался висеть над холстом с
    /// рамкой и ручками, и убрать его можно было только другим действием.
    ///
    /// Именно прижать, а не отменить: Escape уже есть и возвращает пиксели на место, а
    /// «снять выделение» - это «я закончил», как и клик мимо рамки.
    /// </summary>
    [RelayCommand] private void Deselect()
    {
        Document.CommitFloating();
        Document.Selection = null;
        InvalidateCanvas?.Invoke();
    }
    [RelayCommand] private void DeleteSelection()
    {
        if (Document.FloatingPickup is not null)
        {
            if (UndoOwnedPickup()) return;
            // Drop the lifted pixels and keep the hole, recorded so it can be undone.
            Document.DiscardFloating();
            InvalidateCanvas?.Invoke();
            return;
        }
        if (Document.Selection is null) return;
        EraseSelection(); // undoable erase of the selected region
    }

    /// <summary>
    /// Убрать пикап, который создала команда истории (вставка), вместе с самой записью.
    /// False - пикап поднял пользователь, убирать его должен вызывающий.
    ///
    /// Delete, Ctrl+X и Escape по только что вставленной картинке снимали пикап напрямую:
    /// с холста она пропадала, а запись «Вставка» оставалась текущей. История утверждала,
    /// что вставка применена, документ считался изменённым, и Ctrl+Y картинку не возвращал -
    /// курсор-то не двигался. То же правило, по которому это решает первый Ctrl+Z
    /// (<see cref="HistoryManager.Undo"/>).
    ///
    /// Вставка - последняя запись далеко не всегда: между ней и отказом от неё успевает
    /// лечь другая правка, тот же ползунок прозрачности слоя. Отменять «текущую» тогда
    /// нельзя - она чужая, - и прежняя версия просто отступалась, возвращая false. Дальше
    /// вызывающий снимал пикап сам, и получалась ровно та же расходящаяся история, от
    /// которой этот метод и написан: картинки нет, запись есть. Запись вычёркивается
    /// теперь из ленты целиком (<see cref="HistoryManager.Forget"/>) - вставка не тронула
    /// ни одного пикселя, и записям после неё её исчезновение ничем не грозит.
    /// </summary>
    private bool UndoOwnedPickup()
    {
        if (Document.FloatingPickup is not { Owner: { } owner }) return false;
        if (ReferenceEquals(Document.History.Current, owner))
        {
            Document.History.Undo(Document);
        }
        else
        {
            // Пиксели возвращать некуда и незачем: вставка ничего с холста не снимала.
            Document.DropFloating();
            Document.History.Forget(owner);
        }
        AfterHistoryWalk();
        return true;
    }
    [RelayCommand] private void CommitFloating()
    {
        // Enter при обведённой рамке кадрирования - «Обрезать».
        if (_cropTool.HasPending) { ApplyCrop(); return; }
        Document.CommitFloating();
        InvalidateCanvas?.Invoke();
    }
    [RelayCommand] private void CancelFloating()
    {
        // Escape при обведённой рамке кадрирования - «Отмена».
        if (_cropTool.HasPending) { CancelCrop(); return; }
        if (Document.FloatingPickup is null) { Document.Selection = null; return; }
        if (UndoOwnedPickup()) return;
        // Escape must leave no trace: the lifted pixels go back where they came from.
        Document.CancelFloating();
        InvalidateCanvas?.Invoke();
    }

    /// <summary>
    /// Повернуть поднятое выделение: «[» и «]» на ∓90°, с Shift - на ∓15°. Хоткей описан
    /// и в спеке, и в Electron-версии, а в C#-версии его не было вовсе: нажатие не делало
    /// ничего и молчало об этом.
    ///
    /// Выделение, которое ещё не поднимали, поднимается само - иначе хоткей молчал бы там,
    /// где рамка на экране есть. Вместе с подъёмом включается «Выделение»: повёрнутые
    /// пиксели надо чем-то двигать и прижимать, а кисть с ними ничего не умеет.
    /// Угол четырёхугольника отдельно поворачивать не нужно - маска крутится вместе с
    /// объектом (<see cref="Document.DrawPickup"/>).
    /// </summary>
    [RelayCommand]
    private void RotateFloating(string? degrees)
    {
        if (!float.TryParse(degrees, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var deg)) return;

        if (Document.FloatingPickup is null)
        {
            if (ToolContext.DrawTarget() is null) return;
            switch (Document.Selection)
            {
                case RectSelection rs:
                    if (ActiveTool is not (ToolKind.Select or ToolKind.Quad)) ActiveTool = ToolKind.Select;
                    PickupOps.PromoteRect(Document, rs.Rect);
                    break;
                case PolygonSelection ps:
                    if (ActiveTool is not (ToolKind.Select or ToolKind.Quad)) ActiveTool = ToolKind.Quad;
                    PickupOps.PromoteQuad(Document, ps.Corners);
                    break;
                default:
                    // Поворачивать нечего, и раньше об этом не говорилось ни слова:
                    // нажатие «[» или «]» на документе без выделения не делало ровно
                    // ничего. Хоткей мало кому известен, и молчание в ответ читается как
                    // «не работает». Остальные отказы объясняют себя с 1.19.0.
                    ShowHint("Поворачивать нечего — выделите область или вставьте картинку");
                    return;
            }
        }
        if (Document.FloatingPickup is not { } fp) return;

        // Поворот - такая же трансформация, как перетаскивание: исходную область пора стереть.
        PickupOps.EnsureLazyErase(Document, fp);
        fp.SetRotation(fp.Rotation + deg * MathF.PI / 180f);
        Document.NotifyFloatingChanged();
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

    /// <summary>
    /// Применить растягивание холста ручкой. Возвращает false, если применять нечего.
    ///
    /// Отдельно от диалога: там размер вводят числами и промах надо объяснять словами, а
    /// здесь размер уже ограничен самой ручкой - предложить недопустимое она не может.
    /// </summary>
    public bool ResizeCanvasTo(int newWidth, int newHeight, int offsetX, int offsetY)
    {
        if (newWidth == Document.CanvasWidth && newHeight == Document.CanvasHeight) return false;
        if (!Commands.ResizeCanvasCommand.IsAllowed(newWidth, newHeight)) return false;

        // Поднятый объект прижимаем до смены размера: пересборка слоёв заменяет их
        // объекты, и объект остался бы ссылаться на выброшенный битмап.
        Document.CommitFloating();
        Document.History.ExecuteAndPush(
            DocumentTransform.ResizeCanvas(Document, newWidth, newHeight, offsetX, offsetY), Document);
        InvalidateCanvas?.Invoke();
        return true;
    }

    /// <summary>Поля «ширина» и «высота» в правой панели (1.28.0, как в Electron-версии).</summary>
    [ObservableProperty] private string _canvasWidthText = Document.DefaultWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
    [ObservableProperty] private string _canvasHeightText = Document.DefaultHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void SyncCanvasSizeText()
    {
        CanvasWidthText = Document.CanvasWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CanvasHeightText = Document.CanvasHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// «Изменить размер» из полей панели. Не число - сказать и вернуть в поля нынешний
    /// размер; тот же размер - не правка; вне пределов - сказать, как в диалоге.
    /// </summary>
    [RelayCommand] private void ApplyCanvasSize()
    {
        if (!int.TryParse(CanvasWidthText?.Trim(), out var nw) || !int.TryParse(CanvasHeightText?.Trim(), out var nh))
        {
            Views.GlassMessage.Show("Ширина и высота пишутся целыми числами, в пикселях.",
                "Не понял размер", MessageBoxButton.OK, MessageBoxImage.Warning);
            SyncCanvasSizeText();
            return;
        }
        ApplyNewCanvasSize(nw, nh);
    }

    [RelayCommand] private void ResizeCanvas()
    {
        var (w, h) = (Document.CanvasWidth, Document.CanvasHeight);
        var input = Views.PromptDialog.Show(
            "Новый размер холста в формате ШxВ (например, 1200x800):",
            "Изменить размер холста",
            $"{w}x{h}");
        if (string.IsNullOrWhiteSpace(input)) return;
        if (!TryParseCanvasSize(input, out var nw, out var nh))
        {
            Views.GlassMessage.Show(
                "Размер пишется двумя числами через «x»: например, 1200x800.",
                "Не понял размер", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ApplyNewCanvasSize(nw, nh);
    }

    /// <summary>Общая часть диалога и полей панели. True - размер сменился.</summary>
    public bool ApplyNewCanvasSize(int nw, int nh)
    {
        // Тот же самый размер - не правка: команда пересобирает все слои, пишет в историю
        // «Изменение размера», объявляет документ изменённым и заодно снимает выделение и
        // поднятый объект. Диалог открывается с текущим размером в поле, так что нажать
        // OK, ничего не поменяв, - самый обычный способ передумать.
        if (nw == Document.CanvasWidth && nh == Document.CanvasHeight) return false;
        if (!Commands.ResizeCanvasCommand.IsAllowed(nw, nh))
        {
            Views.GlassMessage.Show(
                $"Размер должен быть от 1 до {Commands.ResizeCanvasCommand.MaxDimension} по каждой стороне " +
                $"и не больше {Commands.ResizeCanvasCommand.MaxPixels / 1_000_000} млн пикселей всего.",
                "Слишком большой холст", MessageBoxButton.OK, MessageBoxImage.Warning);
            SyncCanvasSizeText();
            return false;
        }
        Document.CommitFloating();
        var cmd = new ResizeCanvasCommand(nw, nh, SKColors.White);
        Document.History.ExecuteAndPush(cmd, Document);
        InvalidateCanvas?.Invoke();
        return true;
    }

    /// <summary>
    /// Разобрать «ШxВ» из диалога смены размера.
    ///
    /// Русская «х» принимается наравне с латинской «x»: подсказка в диалоге написана
    /// по-русски, и раскладка у пользователя в этот момент тоже русская. «1200х800»,
    /// набранное не переключаясь, разбиралось на одну часть, диалог молча закрывался, и
    /// холст оставался прежним - без единого слова о том, что не так. Заодно «×» и «*»:
    /// размер пишут и так. Отказ теперь виден: раньше любая опечатка была неотличима от
    /// «Отмена».
    /// </summary>
    public static bool TryParseCanvasSize(string input, out int width, out int height)
    {
        width = height = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var parts = input.Split(
            new[] { 'x', 'X', 'х', 'Х', '×', '*' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), out width)
            && int.TryParse(parts[1].Trim(), out height);
    }

    /// <summary>Called by host when the user finished typing for the TextTool prompt.</summary>
    public void ProvideText(string text) => _textTool.CommitText(text);

    /// <summary>Надпись из окна текста: шрифт, размер, B / I / U.</summary>
    public void ProvideText(string text, Tools.TextStyle style) => _textTool.CommitText(text, style);
}
