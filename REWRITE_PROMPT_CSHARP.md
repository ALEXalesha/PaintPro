# Задача

Напиши Windows-приложение «Paint Pro» — растровый графический редактор с GUI в
стиле Apple Liquid Glass (iOS 26 / macOS Tahoe 26) на **C# 12 + WPF (.NET 8) + SkiaSharp**.
Это перенос существующего Electron-приложения (HTML/JS, ~3500 строк в одном файле)
на нативный стек, с архитектурной переработкой — слои, MVVM, Command Pattern для
undo, чистая state-machine.

Цель: меньший размер (~15-30 МБ вместо 71 МБ), нативная производительность,
архитектура которую легко расширять (новые tools, layers, фильтры).

---

# Почему C# + WPF + SkiaSharp

- **SkiaSharp** — тот же graphics engine что в Chrome (Skia). API очень похож
  на HTML5 Canvas (MoveTo/LineTo/Stroke/Fill/clip/setTransform), миграция
  алгоритмов прямая.
- **WPF** — мощный native UI, поддержка blur/acrylic, hardware-accelerated.
- **C#** — strong typing помогает избежать ошибок типа «забыл сбросить state-флаг».
- **MVVM** — обязательная архитектура для WPF, разделение View/Logic.
- **Self-contained publish** дает single .exe ~15-30 МБ против 71 МБ Electron.

NuGet packages (минимум):
- `SkiaSharp` + `SkiaSharp.Views.WPF` — рендеринг через `SKElement` или `SKCanvasView`.
- `Wpf.Ui` — для AcrylicBrush, glass-эффектов.
- `CommunityToolkit.Mvvm` — `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`
  атрибуты — драматически сокращают MVVM boilerplate.

---

# Структура проекта

```
PaintPro.Wpf/
├── App.xaml + App.xaml.cs
├── MainWindow.xaml + MainWindow.xaml.cs
│
├── Models/
│   ├── Document.cs              — document = stack of layers + selection + history
│   ├── Layer.cs                 — abstract layer; PixelLayer : Layer (raster pixels)
│   ├── Selection.cs             — rectangle | polygon | none
│   ├── FloatingPickup.cs        — temporary "pickup" layer (rotated, scaled, warped)
│   └── HistoryEntry.cs          — command-based undo entry
│
├── ViewModels/
│   ├── MainViewModel.cs         — root, owns Document, ActiveTool, etc.
│   ├── LayerListViewModel.cs    — справа: список слоёв с visibility/opacity
│   ├── ColorPickerViewModel.cs  — палитра + recent colors + custom color
│   └── ToolPanelViewModel.cs    — левая панель: список инструментов
│
├── Views/
│   ├── CanvasView.xaml          — SKElement + overlay для selection-frame
│   ├── ToolPanel.xaml           — левая стеклянная панель
│   ├── LayerPanel.xaml          — правая стеклянная панель
│   ├── ColorPalette.xaml
│   └── StatusBar.xaml
│
├── Tools/
│   ├── ITool.cs                 — OnPointerDown/Move/Up, OnActivate/Deactivate
│   ├── PencilTool.cs
│   ├── BrushTool.cs
│   ├── MarkerTool.cs
│   ├── EraserTool.cs
│   ├── FillTool.cs              — flood fill
│   ├── PickerTool.cs            — eyedropper
│   ├── TextTool.cs
│   ├── ShapeTool.cs             — общая база для Line/Rect/Ellipse/Triangle/...
│   ├── SelectTool.cs            — rectangle selection
│   ├── QuadTool.cs              — 4-point polygon selection
│   └── CropTool.cs
│
├── Commands/                    — Command Pattern для undo/redo
│   ├── ICommand.cs              — { Execute(); Undo(); }
│   ├── DrawStrokeCommand.cs
│   ├── FillCommand.cs
│   ├── PasteCommand.cs
│   ├── MoveSelectionCommand.cs
│   ├── TransformFloatingCommand.cs
│   ├── ClearCanvasCommand.cs
│   └── ResizeCanvasCommand.cs
│
├── Services/
│   ├── HistoryManager.cs        — Undo()/Redo()/Push() стек
│   ├── ClipboardService.cs      — read/write image clipboard
│   ├── FileService.cs           — open/save PNG/JPEG/BMP/WEBP
│   └── GeometryMath.cs          — anchor-based resize math, rotation, polygon clip
│
├── Converters/                  — XAML value converters
│
├── Resources/
│   ├── Themes.xaml              — цветовые токены
│   ├── GlassStyles.xaml         — .panel, .btn, .tool кнопки
│   └── Icons/                   — SVG → XAML path data для инструментов
│
└── Tests/
    └── PaintPro.Tests/
        ├── GeometryMathTests.cs        — rotated resize math, anchor calculations
        ├── HistoryManagerTests.cs      — undo/redo invariants
        ├── FloodFillTests.cs
        └── ClearCanvasTests.cs         — все state-слои сбрасываются
```

---

# Архитектурные принципы (жёсткие)

## 1. MVVM везде

View биндится к ViewModel через `{Binding}`, никаких `MainWindow.cs` с прямыми
вызовами `_canvasElement.Invalidate()`. ViewModel меняет Model → событие
`PropertyChanged` → View реагирует автоматически.

С `CommunityToolkit.Mvvm` boilerplate минимален:

```csharp
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private ToolKind activeTool = ToolKind.Pencil;
    [ObservableProperty] private Color primaryColor = Colors.Black;
    [ObservableProperty] private int brushSize = 4;

    [RelayCommand] private void Undo() => _history.Undo();
}
```

## 2. Document — это stack of layers

Не один canvas + ad-hoc flags «floating», «selection». Document содержит:

```csharp
public class Document
{
    public ObservableCollection<Layer> Layers { get; }      // background + user layers
    public Selection? Selection { get; set; }               // current selection (null или rect/polygon)
    public FloatingPickup? FloatingPickup { get; set; }     // temporary edit layer
    public HistoryStack History { get; }
    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }
}
```

`FloatingPickup` — это **отдельный лёгкий слой** который рендерится поверх Document
во время трансформации (move/scale/rotate). При commit его pixels сливаются в
active layer.

## 3. Command Pattern для undo

НЕ snapshot всего canvas в PNG (как в Electron). Каждое действие — это команда:

```csharp
public interface IDocumentCommand
{
    void Execute(Document doc);
    void Undo(Document doc);
}

public sealed class DrawStrokeCommand : IDocumentCommand
{
    private readonly SKBitmap _strokeBitmap;  // overlay bitmap со strokeми
    private readonly int _targetLayerIndex;
    private SKBitmap? _previousBitmap;        // saved on Execute, restored on Undo

    public void Execute(Document doc) { /* compose stroke onto layer, save snapshot */ }
    public void Undo(Document doc)    { /* restore _previousBitmap */ }
}
```

История = стек команд. Память экономится: для маленького штриха не хранится
весь canvas, только diff-bitmap размером со штрих.

## 4. Tools через ITool

Каждый инструмент изолирован:

```csharp
public interface ITool
{
    string Name { get; }
    void OnPointerDown(SKPoint pos, ToolContext ctx);
    void OnPointerMove(SKPoint pos, ToolContext ctx);
    void OnPointerUp(SKPoint pos, ToolContext ctx);
    void OnActivate(ToolContext ctx);
    void OnDeactivate(ToolContext ctx);   // при смене tool — может закоммитить floating и т.п.
    SKBitmap? PreviewBitmap { get; }      // что показывать поверх canvas во время drag
    Cursor? GetCursor(SKPoint pos);
}
```

`ToolContext` — это «view» в Document/state, безопасный для инструмента.
Никаких global mutable variables.

## 5. State-машина для selection / floating

Одно enum-поле вместо разбросанных bool-флагов:

```csharp
public enum DocumentMode
{
    Idle,                  // ничего не выделено
    SelectionRect,         // активный rect selection (selection != null, floating == null)
    SelectionPolygon,      // активный polygon selection (quad)
    FloatingActive,        // pickup поднят, идёт move/scale/rotate
    DrawingShape,          // активный shape drag (rect/ellipse/line/...)
    Cropping
}
```

Каждый переход — явный метод (`EnterFloating()`, `ExitFloating()`).
Никаких `state.draggingFloating + state.resizingFloating + state.warpingQuad`.

---

# Запрещено

1. **Логика в code-behind View** (например в `MainWindow.xaml.cs` нельзя
   `_brushSize = 5; UpdateCursor();`). Только PropertyChanged биндинги.

2. **`global static`** state. Только DI или ViewModel-локальное.

3. **`Application.DoEvents()`, `Thread.Sleep(...)` в UI thread**.

4. **`BitmapImage` из URI с reload каждый кадр**. Используй `SKBitmap` напрямую
   или кэшируй.

5. **`Canvas.Children.Add(new Line(...))`** для рисования штрихов. Это создаёт
   тысячи WPF-элементов и тормозит. **Только SkiaSharp** на single `SKElement`.

6. **Inline style на XAML элементах**. Только через `Style="{StaticResource ...}"`.

7. **PNG-snapshot для undo** размером в canvas. Только diff-bitmap размером со
   strokeBox.

---

# Функциональные требования (всё что есть в текущем Paint Pro Electron)

## Инструменты рисования
- **Pencil** — тонкая линия, `lineWidth = size * 0.5`, `lineCap = round`.
- **Brush** — толстая мягкая линия, `lineWidth = size`, round caps/joins.
- **Marker** — полупрозрачный (`alpha = 0.4`), рисуется на отдельный bitmap при
  `alpha=1` (чтобы overlapping не накапливался), потом merge'ится через
  `SaveLayerAlpha`.
- **Eraser** — рисует белым (в Paint классически, не透ность).
- **Fill** — flood fill, классический BFS/scanline algorithm.
- **Picker** — eyedropper, читает `SKBitmap.GetPixel()`.
- **Text** — отдельный editable text box overlay (XAML TextBox), при commit
  растрируется на bitmap через `SKCanvas.DrawText`.

## Фигуры (все рисуются drag)
- **Line** — прямая
- **Rect** — прямоугольник (outline и/или fill)
- **Ellipse** — эллипс
- **Triangle** — треугольник
- **Star** — 5-конечная звезда
- **Arrow** — стрелка
- **Heart** — сердце

## Выделение
### Select (S) — прямоугольное
- Drag → bbox с **2px dashed border** (anti-marching-ants для стабильности —
  не делай background-image-keyframe animation).
- Внутри drag → move pixels (через `SKBitmap.Extract` + `Canvas.DrawBitmap`).
- 8 resize-handles по углам/сторонам + 1 rotate-handle сверху.
  **СКРЫВАЙ handles во время drawing/move** (т.е. пока зажата кнопка мыши и
  выделение растёт) — иначе при tiny drag mouse попадает на handle, события
  не доходят до canvas. Через `IsHitTestVisible = false` или CSS-эквивалент.
- Сlick на handle → promote во floating с anchor-based resize math.

### Quad (Q) — полигон 4 точками
- Drag → bbox (как у Select), потом 4 угла превращаются в drag-targets.
- Каждый угол можно тащить независимо → меняется ФОРМА clip-маски (НЕ warp/scale).
- 4 угла = 4 крупные круглые точки (18×18).
- Плюс 8 bbox-handles по периметру → uniform scale всего полигона.
- Плюс 1 rotate-handle.
- Содержимое НЕ деформируется — pickup рисуется в исходных координатах,
  quad работает как `SKCanvas.ClipPath`.

### Photoshop-like polygon-selection
- При создании quad: ничего не стирается, pickup идентичен canvas в той же
  области (визуально без изменений).
- На **первое translate/scale/rotate** (НЕ corner-drag!) лениво
  снимается snapshot `OriginalQuad = currentQuad`, и в active layer canvas
  стирается **именно эта polygon-shape** (НЕ bbox). При cancel preCanvas backup
  восстанавливает.

## Трансформации
- **Rotate**: кругленькая ручка сверху selection-bbox. Drag → угол вокруг
  центра bbox. Hotkeys `[` / `]` = ±90°, `Shift+[`/`Shift+]` = ±15°.
- **Resize**: 8 handles, anchor-based math (противоположный угол фиксирован,
  мышь приводится к локальной системе через -rotation, считается новый размер,
  центр возвращается в world через +rotation). Работает в любом повороте.
  Должно работать для quad: scale 4 точки вокруг того же anchor.
- **Move**: click внутри selection + drag.
- При любой трансформации **lazy-erase**: оригинальная область стирается на
  active layer при первом действии (не при создании floating).

## Цвет
- Палитра 40 предустановленных цветов.
- Custom color picker (system `ColorDialog` или WPF Color Picker).
- **Recent colors** (8 последних).
- Current color preview.

## Прозрачность
- `Opacity` slider 1-100% для всех drawing tools.

## Размер
- `Size` slider 1-100 px. Кэшируй отдельные значения для brush vs eraser
  (brushSize, eraserSize), чтобы переключение tool не сбивало размер.

## Масштаб (zoom)
- Кнопки `+`/`-`, `1:1`. `Ctrl+wheel` = zoom.
- Сохраняй центр под курсором при zoom (не упрощённое scale-from-origin).
- Discrete steps: [0.1, 0.25, 0.5, 0.67, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0].
- Hand tool (`H`) — pan через drag.

## Холст (canvas)
- Default 900×600.
- Resize: edge-handles справа/снизу/угол. При drag — preview-rect с tooltip
  `WxH`. На mouseup — реальный resize с белой заливкой пустых областей.
- Sidebar: ширина/высота как input fields + Apply button.

## Быстрые действия (sidebar)
- Повернуть 90°
- Отразить горизонтально
- Отразить вертикально

## File I/O
- Open: `OpenFileDialog` PNG/JPG/BMP/GIF/WEBP. Если изображение больше canvas
  — спросить «расширить?» или «вписать?». Если меньше — paste в (0,0).
- Save: `SaveFileDialog`. Если был открыт файл — `Ctrl+S` пишет в него.
- File associations (через app.manifest): `.png`, `.jpg`, `.jpeg`, `.bmp`, `.gif`, `.webp`.
- Drag-and-drop файлов на окно — открывает.

## Clipboard
- `Ctrl+C` — копирует selection (или весь canvas если нет selection).
- `Ctrl+X` — вырезает.
- `Ctrl+V` — вставляет как FloatingPickup в (20, 20).

## Меню (Menu Bar)
- Файл: Создать / Открыть / Сохранить / Сохранить как / Экспорт / Выход
- Правка: Undo / Redo / Cut / Copy / Paste / Delete / Select All / Deselect
- Вид: Zoom / Fit / Reset zoom / Toggle grid / Toggle sidebar
- Изображение: Resize canvas / Rotate / Flip / Crop to selection
- Справка: О программе

## Хоткеи (КРИТИЧНО — все должны работать СТАБИЛЬНО)
- `S` — select, `Q` — quad, `H` — hand, `P` — pencil, `B` — brush, `M` — marker,
  `E` — eraser, `G` — fill, `I` — picker, `T` — text
- `Ctrl+Z` — undo, `Ctrl+Y` / `Ctrl+Shift+Z` — redo
- `Ctrl+S` — save, `Ctrl+O` — open
- `Ctrl+C/X/V` — clipboard
- `Ctrl+A` — select all
- `Delete` — clear selection contents / cancel floating
- `Escape` — commit / cancel
- `Enter` — commit floating
- `[` / `]` — rotate ±90° (Shift = ±15°)
- `Ctrl+=` / `Ctrl+-` / `Ctrl+0` — zoom in/out/reset
- В WPF используй `InputBindings` на Window-уровне + `Focusable=false` на
  кнопках, чтобы хоткеи не перехватывались focus-ом кнопок.

## Статусбар
- Pos: `X, Y` под курсором
- Selection: `WxH` если есть
- Pixel color preview: swatch + hex код пикселя под курсором.
  **Throttle через `DispatcherTimer` (16ms ≈ 60 fps)**, иначе `GetPixel` 
  каждый mousemove тормозит canvas.
- Версия / кнопка справки

---

# Визуальный язык — Apple Liquid Glass (iOS 26 / macOS Tahoe 26)

## Цветовые токены (как XAML resources)

```xaml
<SolidColorBrush x:Key="Accent"          Color="#5B8DEF"/>
<SolidColorBrush x:Key="Accent2"         Color="#9D5BEF"/>
<LinearGradientBrush x:Key="AccentGrad" StartPoint="0,0" EndPoint="1,1">
    <GradientStop Offset="0" Color="#5B8DEF"/>
    <GradientStop Offset="1" Color="#9D5BEF"/>
</LinearGradientBrush>
<SolidColorBrush x:Key="TextPrimary"     Color="#F4F4F8"/>
<SolidColorBrush x:Key="TextDim"         Color="#9EA0AE"/>
<SolidColorBrush x:Key="GlassBg"         Color="#14FFFFFF"/>
<SolidColorBrush x:Key="GlassBgStrong"   Color="#24FFFFFF"/>
<SolidColorBrush x:Key="GlassBorder"     Color="#2EFFFFFF"/>
```

## Window background (обязательно цветной)

Используй `<RadialGradientBrush>` накладкой 4-5 цветных пятен поверх базового
LinearGradient `#0E0C1A → #1A1330 → #0C1024` (135°). Если просто чёрный фон —
acrylic blur будет визуально серым.

Пятна (примерно):
- 1100×700 от (12%, -10%): rgba(120,90,255, 0.55) → transparent
- 900×600  от (95%, 8%):    rgba(255,100,180, 0.40) → transparent
- 800×800  от (85%, 110%):  rgba(60,200,255, 0.40) → transparent
- 700×600  от (5%, 105%):   rgba(100,255,180, 0.30) → transparent

Реализуй через накладывающиеся `<Ellipse>` с radial fill на `Canvas` или
через несколько `<Rectangle>` с `OpacityMask`. WPF не имеет нативного
multi-stop multiple radial gradients как CSS.

## Стеклянная панель `.panel`

```xaml
<Style x:Key="GlassPanel" TargetType="Border">
    <Setter Property="Background"      Value="{StaticResource GlassBg}"/>
    <Setter Property="BorderBrush"     Value="{StaticResource GlassBorder}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="CornerRadius"    Value="18"/>
    <Setter Property="Margin"          Value="8"/>
    <Setter Property="Effect">
        <Setter.Value>
            <DropShadowEffect BlurRadius="32" ShadowDepth="8" Opacity="0.35" Color="Black"/>
        </Setter.Value>
    </Setter>
</Style>
```

Если используешь `Wpf.Ui` — пакет имеет `AcrylicBrush` который даёт настоящий
blur+saturate. Иначе fallback на полупрозрачный + `BlurEffect` на снапшоте
фона (медленнее).

## Бегущие пузыри в фоне

8-12 `<Ellipse>` с radial gradient fill на отдельном `Canvas` (z-index 0).
Каждый со своим `Storyboard` который:
- `TranslateTransform.Y`: From=`WindowHeight + 200`, To=`-200`, Duration=18-30s
- `Opacity`: 0 → 0.85 → 0.6 → 0 (через keyframes)
- `EasingFunction`: `SineEase` или `QuadraticEase`
- `RepeatBehavior=Forever`

Не делай через `DispatcherTimer` и manual Update — это лагает и съедает CPU.
WPF Storyboard работает на compositor thread, идеально 60 fps.

## Анимация ripple на клике

При MouseDown на любой `.btn`/`.tool` — `<Ellipse>` за курсором, scale 0 → 2.6,
opacity 0.9 → 0. Через `Storyboard` 650ms. Управляй через behavior или
attached property.

---

# КРИТИЧНЫЕ АНТИПАТТЕРНЫ из Electron-версии (НЕ ПОВТОРЯТЬ)

Эти баги мы прошли в HTML-версии после ~30 итераций фиксов. **Каждый из них —
прямой указатель на архитектурное решение в C#**.

## 1. Сlear на одном слое visible state НЕ сбрасывает все state-слои

В Electron `clearCanvas()` заливал основной `<canvas>` белым, но игнорировал
`previewCanvas` (для floating pickup), `state.selectionData` (ImageData),
`state.originalBeforeMove` (backup). Визуально пользователь видел «ничего
не очистилось» — старый floating оставался поверх.

**В C# фикс**: `ClearCanvasCommand.Execute(doc)` должен:
```csharp
doc.FloatingPickup?.Dispose();
doc.FloatingPickup = null;
doc.Selection = null;
doc.ActiveLayer.Clear(SKColors.White);
doc.Mode = DocumentMode.Idle;
```
И **unit-test**: «после Clear с активным floating, нажми Pencil + рисуй —
floating не появляется поверх, рисую на чистом холсте».

## 2. Handles внутри overlay перехватывают mousemove во время drag

Когда у selection-overlay есть handles в углах с `IsHitTestVisible=true` и
`pointer-events:auto`, при tiny drag (2-3 px) cursor оказывается на handle,
mousemove/mouseup стреляют на handle, canvas не получает событий, selection
застывает на 2-х пикселях.

**В C# фикс**: handles должны быть `IsHitTestVisible=False` пока идёт active
drawing (привязано к флагу `IsDrawing` через PropertyChanged → trigger в style):

```xaml
<Style.Triggers>
    <DataTrigger Binding="{Binding IsDrawing}" Value="True">
        <Setter TargetName="ResizeHandles" Property="IsHitTestVisible" Value="False"/>
        <Setter TargetName="ResizeHandles" Property="Visibility" Value="Collapsed"/>
    </DataTrigger>
</Style.Triggers>
```

ИЛИ используй `CaptureMouse()` на canvas в `PointerDown` — это берёт capture,
все events идут только на canvas пока pointer не released. **Правильный
WPF-way**.

## 3. Перехват фокуса кнопками ломает хоткеи

В HTML после клика на любую `<button>` фокус остаётся на кнопке, и хоткеи
(Q, P, B, etc.) либо вообще не доходят, либо триггерят активацию кнопки.

**В C# фикс**: на всех Toolbar-кнопках поставь `Focusable="False"`. Хоткеи
вешай на `Window.InputBindings`, не на кнопки:

```xaml
<Window.InputBindings>
    <KeyBinding Key="Q" Command="{Binding SelectQuadToolCommand}"/>
    <KeyBinding Key="S" Command="{Binding SelectSelectToolCommand}"/>
    <KeyBinding Key="Z" Modifiers="Ctrl" Command="{Binding UndoCommand}"/>
    ...
</Window.InputBindings>
```

## 4. Floating + Selection одновременно живут в state

В Electron у меня были одновременно `state.floating` и `state.selection`,
причём логика переключения была разбросана. Постоянно был баг: «commit floating
не сбрасывает selection» или наоборот.

**В C# фикс**: они **взаимоисключающие**. Property setter поддерживает invariant:

```csharp
public Selection? Selection
{
    get => _selection;
    set
    {
        if (value != null && FloatingPickup != null)
            CommitFloating();   // нельзя иметь оба
        SetProperty(ref _selection, value);
    }
}

public FloatingPickup? FloatingPickup
{
    get => _floating;
    set
    {
        if (value != null) Selection = null;
        SetProperty(ref _floating, value);
    }
}
```

## 5. PNG-snapshot для undo

В Electron `state.history` = `[canvas.toDataURL(), ...]` — каждый snapshot
~3 МБ. Через 30 операций — 90 МБ памяти и медленный restore.

**В C# фикс**: Command Pattern. Каждая команда хранит только diff
(bitmap размером со stroke, не размером с canvas):

```csharp
public sealed class DrawStrokeCommand : IDocumentCommand
{
    private readonly SKBitmap _strokeBitmap;
    private readonly SKRect _strokeBounds;
    private SKBitmap? _underlyingPixels;  // только пиксели под штрихом, snapshot ДО execute

    public void Execute(Document doc)
    {
        var layer = doc.ActiveLayer;
        _underlyingPixels = layer.ExtractRegion(_strokeBounds);  // ~ KB не MB
        layer.DrawBitmap(_strokeBitmap, _strokeBounds);
    }
    public void Undo(Document doc) => doc.ActiveLayer.DrawBitmap(_underlyingPixels!, _strokeBounds);
}
```

## 6. Pickup для polygon-selection стирает bbox вместо polygon

В Electron при создании quad-selection я сразу стирал весь bbox — поэтому
если ты делал треугольник corner-drag'ом, вокруг треугольника был белый
прямоугольник на canvas.

**В C# фикс**: lazy-erase на первое translate/scale/rotate, с snapshot
**текущей** polygon-shape (а не bbox-формы). Реализуй через флаг
`OriginalQuadErased` и метод `EnsureOriginalQuadErased()`. См. полный код
в существующем `paint-pro.html` (функция с тем же именем).

## 7. State.drawing + state.draggingFloating + state.movingSelection — 6 разных bool флагов

Это race conditions в чистом виде. Один сбросил, другой нет.

**В C# фикс**: один enum `DocumentMode`, один setter, явные переходы.

---

# Конкретные тесты которые ОБЯЗАТЕЛЬНО должны пройти

```csharp
[Fact]
public void Clear_resets_all_layers_including_floating()
{
    var doc = new Document(900, 600);
    doc.ActiveLayer.DrawRect(new SKRect(100,100,200,200), SKColors.Red);
    // Создаём floating-pickup
    doc.Selection = new RectSelection(50,50,300,300);
    new PromoteSelectionCommand().Execute(doc);
    Assert.NotNull(doc.FloatingPickup);

    new ClearCanvasCommand().Execute(doc);

    Assert.Null(doc.FloatingPickup);
    Assert.Null(doc.Selection);
    Assert.Equal(DocumentMode.Idle, doc.Mode);
    // Canvas полностью белый
    Assert.True(doc.ActiveLayer.IsAllWhite());
}

[Fact]
public void Rotated_resize_anchor_math_preserves_opposite_corner()
{
    // floating 100x100 в (50,50), rotation = 30°
    // тащим SE handle на (200,200) — newCorner = (200,200)
    // ожидаем что NW corner (anchor) остался в world coords (50,50) после resize
    var floating = new FloatingPickup { X=50, Y=50, W=100, H=100, Rotation=Math.PI/6 };
    var math = new ResizeMath(floating, "se");
    math.UpdateMouse(new SKPoint(200, 200));
    var nwInWorld = math.GetCornerWorldPosition("nw");
    Assert.Equal(50, nwInWorld.X, 1);  // tolerance 1px
    Assert.Equal(50, nwInWorld.Y, 1);
}

[Fact]
public void Quad_corner_drag_does_NOT_erase_canvas()
{
    var doc = SetupDocWithImage();
    new PromoteQuadCommand().Execute(doc);  // создаёт floating с quad
    var canvasBefore = doc.ActiveLayer.Snapshot();

    // Тащим один corner — НЕ должно менять active layer canvas
    doc.FloatingPickup!.Quad[0] = new SKPoint(20, 20);
    var canvasAfter = doc.ActiveLayer.Snapshot();
    Assert.True(canvasBefore.Equals(canvasAfter));
}

[Fact]
public void Quad_translate_erases_original_polygon_NOT_bbox()
{
    // Создаём quad, делаем corner-drag в треугольник, потом translate.
    // На canvas в исходной позиции остался "всё минус треугольник", не "всё минус bbox".
    var doc = SetupDocWithImage();
    new PromoteQuadCommand().Execute(doc);
    var floating = doc.FloatingPickup!;
    floating.Quad[0] = floating.Quad[1];  // схлопнули → треугольник
    // первый translate
    doc.MoveFloating(new SKVector(100, 0));
    // canvas в исходной bbox: pixels вне треугольника сохранились
    var pixelOutsideTriangle = doc.ActiveLayer.GetPixel(floating.OriginalBBox.X, floating.OriginalBBox.Y);
    Assert.NotEqual(SKColors.White, pixelOutsideTriangle);  // оригинальный pixel
}

[Fact]
public void Hotkey_works_even_after_clicking_button()
{
    var window = new MainWindow();
    var vm = (MainViewModel)window.DataContext;
    // Симулируем клик на кнопке Brush
    window.BrushToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Assert.Equal(ToolKind.Brush, vm.ActiveTool);
    // Теперь нажимаем "S" — должен переключиться на Select
    window.RaiseEvent(new KeyEventArgs(... Key.S ...));
    Assert.Equal(ToolKind.Select, vm.ActiveTool);  // фокус не блокирует
}

[Fact]
public void Selection_and_floating_are_mutually_exclusive()
{
    var doc = new Document(900, 600);
    doc.Selection = new RectSelection(0,0,100,100);
    Assert.NotNull(doc.Selection);
    Assert.Null(doc.FloatingPickup);

    doc.FloatingPickup = new FloatingPickup { ... };
    Assert.Null(doc.Selection);  // selection auto-cleared
    Assert.NotNull(doc.FloatingPickup);
}
```

---

# Финальный результат

1. Solution `PaintPro.sln` открывается в Visual Studio 2022 или VS Code C# Dev Kit.
2. `dotnet run` запускает приложение.
3. `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
   -p:IncludeNativeLibrariesForSelfExtract=true` даёт single-file .exe ~25-30 МБ.
4. README с архитектурой, скриншот, инструкции «как добавить новый tool».
5. Все unit-тесты зелёные.

## Roadmap (необязательно к первой версии, но архитектура должна позволять)

- Layers panel (multi-layer support)
- Layer blend modes
- Filters (Gaussian blur, sharpen, brightness/contrast)
- Free-form lasso (полигон с N точек)
- Magic Wand (color tolerance selection)
- Path tool (Bezier curves)
- Export to SVG
- Image tiling export

---

# Reference материалы

Если есть доступ к существующему `paint-pro-electron/paint-pro.html` —
там ~3500 строк HTML/CSS/JS со всей логикой. Это твой "spec". НЕ переноси
дословно — это монолит. Переноси **поведение и hot-key set**, но архитектуру
строй с нуля по принципам выше.

Особенно полезные файлы для reference:
- `DEV_CONTEXT.md` — описание визуального языка, токенов, антипаттернов.
- `paint-pro.html` — функция `resizeFloating()` (anchor-based math),
  `drawClippedQuad()` (polygon clip), `ensureOriginalQuadErased()`
  (lazy erase для polygon).

# Начни с

1. Solution skeleton с пустыми классами (Document, Layer, ITool, IDocumentCommand)
   и unit-тестами на математику (GeometryMath).
2. CanvasView с SkiaSharp — нарисовать тестовый rect, проверить что рендеринг
   работает.
3. PencilTool как самый простой — реализовать полный цикл PointerDown/Move/Up
   с DrawStrokeCommand → HistoryManager.Push.
4. Undo/Redo на тестах.
5. Selection tool с rect + handles + lazy promote во FloatingPickup.
6. Quad tool с polygon clip.
7. Все остальные tools.
8. Стилизация Liquid Glass — в самом конце, когда логика отлажена.

**Не пытайся всё сделать в один файл MainWindow.xaml.cs. Не используй только
code-behind. Цель — код который через 6 месяцев легко расширить новым tool
(нарисовал ITool, добавил кнопку — работает) без рефакторинга всего проекта.**
