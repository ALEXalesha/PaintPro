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
    private readonly HandTool _handTool = new();

    /// <summary>Raised when the active tool changes. CanvasView re-subscribes pointer events.</summary>
    public event Action? ToolChanged;
    /// <summary>Raised when canvas needs an immediate visual refresh (rare; usually PropertyChanged handles it).</summary>
    public event Action? InvalidateCanvas;
    /// <summary>Raised when the TextTool wants a string from the user at <see cref="SKPoint"/>.</summary>
    public event Action<SKPoint>? TextRequested;
    /// <summary>Raised when the HandTool pans by <see cref="SKPoint"/> delta (canvas-space pixels).</summary>
    public event Action<SKPoint>? PanRequested;

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
        _handTool.Panned += d => PanRequested?.Invoke(d);

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
            [ToolKind.Hand]    = _handTool,
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
            var item = new LayerListItemViewModel(Document.Layers[i]) { IsActive = i == Document.ActiveLayerIndex };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(LayerListItemViewModel.Visible)
                    or nameof(LayerListItemViewModel.Opacity))
                    InvalidateCanvas?.Invoke();
            };
            LayerItems.Add(item);
        }
    }

    [RelayCommand]
    private void AddLayer()
    {
        var layer = new PixelLayer(Document.CanvasWidth, Document.CanvasHeight, SKColors.Transparent)
        {
            Name = $"Layer {Document.Layers.Count}",
        };
        Document.Layers.Add(layer);
        Document.ActiveLayerIndex = Document.Layers.Count - 1;
        InvalidateCanvas?.Invoke();
    }

    [RelayCommand]
    private void RemoveLayer(LayerListItemViewModel? item)
    {
        if (item is null || Document.Layers.Count <= 1) return;
        var idx = Document.Layers.IndexOf(item.Layer);
        if (idx < 0) return;
        item.Layer.Dispose();
        Document.Layers.RemoveAt(idx);
        Document.ActiveLayerIndex = Math.Clamp(Document.ActiveLayerIndex, 0, Document.Layers.Count - 1);
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
        ToolChanged?.Invoke();
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
            "#1A1330","#0E0C1A","#2EFFFFFF".Substring(0,7), // mid-grey-ish
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
    private void Undo() { Document.History.Undo(Document); InvalidateCanvas?.Invoke(); }
    private bool CanUndo => Document.History.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() { Document.History.Redo(Document); InvalidateCanvas?.Invoke(); }
    private bool CanRedo => Document.History.CanRedo;

    /// <summary>Timeline rows for the History panel; rebuilt whenever the history changes.</summary>
    public ObservableCollection<HistoryEntryViewModel> HistoryItems { get; } = new();

    [RelayCommand]
    private void JumpToHistory(HistoryEntryViewModel? entry)
    {
        if (entry is null) return;
        Document.History.JumpTo(entry.Target, Document);
        InvalidateCanvas?.Invoke();
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
        "Rotate CW"       => "Поворот по часовой",
        "Rotate CCW"      => "Поворот против часовой",
        "Flip horizontal" => "Отражение по горизонтали",
        "Flip vertical"   => "Отражение по вертикали",
        _                 => displayName,
    };

    // ───────── File ─────────
    [RelayCommand] private void NewDocument()
    {
        var cmd = new ClearCanvasCommand();
        Document.History.ExecuteAndPush(cmd, Document);
        InvalidateCanvas?.Invoke();
    }

    [RelayCommand]
    private void Open()
    {
        var bmp = FileService.OpenImageDialog();
        if (bmp is null) return;
        ApplyOpenedBitmap(bmp);
    }

    public void ApplyOpenedBitmap(SKBitmap bmp)
    {
        // Resize canvas to image size for simplicity (spec leaves "expand or fit" as an option).
        var resize = new ResizeCanvasCommand(bmp.Width, bmp.Height, SKColors.White);
        Document.History.ExecuteAndPush(resize, Document);
        if (Document.ActiveLayer is PixelLayer pl)
        {
            // Draw the opened image through history so it can be undone *and* redone.
            var full = new SKRectI(0, 0, pl.Width, pl.Height);
            var before = pl.ExtractRegion(full);
            var after = new SKBitmap(pl.Width, pl.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(after))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(bmp, 0, 0);
            }
            Document.History.ExecuteAndPush(new RegionDiffCommand("Открытие", full, before, after), Document);
        }
        InvalidateCanvas?.Invoke();
    }

    [RelayCommand] private void Save() { if (Document.FloatingPickup is not null) Document.CommitFloating(); FileService.SaveOrSaveAs(Document); }
    [RelayCommand] private void SaveAs() { if (Document.FloatingPickup is not null) Document.CommitFloating(); FileService.SaveAsDialog(Document); }

    // ───────── Clipboard ─────────
    [RelayCommand] private void CopySelection() => ClipboardService.Copy(Document);
    [RelayCommand] private void CutSelection()
    {
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
        switch (Document.Selection)
        {
            case RectSelection rs:
            {
                var b = SKRectI.Intersect(SKRectI.Round(rs.Rect), canvasRect);
                return b.IsEmpty ? null : new EraseRegionCommand(b);
            }
            case PolygonSelection ps:
            {
                var b = SKRectI.Intersect(SKRectI.Round(ps.BoundingBox), canvasRect);
                return b.IsEmpty ? null : new EraseRegionCommand(b, ps.Corners.ToArray());
            }
            default:
                return null;
        }
    }
    [RelayCommand] private void Paste()
    {
        var bmp = ClipboardService.TryGetImage();
        if (bmp is null) return;
        var cmd = new PasteCommand(bmp, new SKPoint(20, 20));
        Document.History.ExecuteAndPush(cmd, Document);
        bmp.Dispose();
        ActiveTool = ToolKind.Select;
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
            // Drop floating without merge.
            Document.FloatingPickup.Dispose();
            Document.FloatingPickup = null;
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
        if (Document.FloatingPickup is null) return;
        Document.FloatingPickup.Dispose();
        Document.FloatingPickup = null;
        InvalidateCanvas?.Invoke();
    }

    // ───────── Image ops ─────────
    [RelayCommand] private void Rotate90()        => RotateActiveLayer(MathF.PI / 2f);
    [RelayCommand] private void RotateMinus90()   => RotateActiveLayer(-MathF.PI / 2f);
    [RelayCommand] private void FlipHorizontal()  => FlipActiveLayer(horizontal: true);
    [RelayCommand] private void FlipVertical()    => FlipActiveLayer(horizontal: false);

    private void RotateActiveLayer(float radians)
    {
        if (Document.ActiveLayer is not PixelLayer pl) return;
        bool quarter = MathF.Abs(MathF.Abs(radians) - MathF.PI / 2f) < 0.01f;
        int newW = quarter ? pl.Height : pl.Width;
        int newH = quarter ? pl.Width  : pl.Height;

        var before = pl.ExtractRegion(new SKRectI(0, 0, pl.Width, pl.Height));
        var after = new SKBitmap(newW, newH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(after))
        {
            c.Clear(SKColors.White);
            c.Translate(newW / 2f, newH / 2f);
            c.RotateRadians(radians);
            c.Translate(-pl.Width / 2f, -pl.Height / 2f);
            c.DrawBitmap(pl.Bitmap, 0, 0);
        }
        var label = radians > 0 ? "Rotate CW" : "Rotate CCW";
        var cmd = new ReplaceActiveLayerCommand(label, before, pl.Width, pl.Height, after, newW, newH);
        Document.History.ExecuteAndPush(cmd, Document);
        InvalidateCanvas?.Invoke();
    }

    private void FlipActiveLayer(bool horizontal)
    {
        if (Document.ActiveLayer is not PixelLayer pl) return;
        var before = pl.ExtractRegion(new SKRectI(0, 0, pl.Width, pl.Height));
        var after = new SKBitmap(pl.Width, pl.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(after))
        {
            c.Clear(SKColors.White);
            if (horizontal) { c.Translate(pl.Width, 0); c.Scale(-1, 1); }
            else            { c.Translate(0, pl.Height); c.Scale(1, -1); }
            c.DrawBitmap(pl.Bitmap, 0, 0);
        }
        var label = horizontal ? "Flip horizontal" : "Flip vertical";
        var cmd = new ReplaceActiveLayerCommand(label, before, pl.Width, pl.Height, after, pl.Width, pl.Height);
        Document.History.ExecuteAndPush(cmd, Document);
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
        var cmd = new ResizeCanvasCommand(nw, nh, SKColors.White);
        Document.History.ExecuteAndPush(cmd, Document);
        InvalidateCanvas?.Invoke();
    }

    /// <summary>Called by host when the user finished typing for the TextTool prompt.</summary>
    public void ProvideText(string text) => _textTool.CommitText(text);
}
