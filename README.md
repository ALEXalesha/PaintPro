# Paint Pro — WPF + SkiaSharp

Растровый графический редактор в стиле Apple Liquid Glass (iOS 26 / macOS Tahoe 26).
Перенос Electron-версии на C# 12 + WPF + SkiaSharp.

## Что внутри

- `src/PaintPro.Wpf/` — основной WPF-проект
  - `Models/` — `Document`, `Layer`, `Selection`, `FloatingPickup`, `DocumentMode`, `ToolKind`
  - `Commands/` — Command Pattern: `DrawStrokeCommand`, `FillCommand`, `ClearCanvasCommand`,
    `ResizeCanvasCommand`, `PasteCommand`, `CropCommand`
  - `Tools/` — `ITool` + 18 реализаций (Pencil, Brush, Marker, Eraser, Fill, Picker, Text,
    Line, Rect, Ellipse, Triangle, Star, Arrow, Heart, Select, Quad, Crop, Hand)
  - `Services/` — `HistoryManager`, `FileService`, `ClipboardService`, `GeometryMath`,
    `SkiaBitmapBridge`
  - `ViewModels/` — `MainViewModel`, `ColorEntryViewModel`
  - `Views/` — `CanvasView` (SkiaSharp + overlay), `PromptDialog`
  - `Resources/` — `Themes.xaml`, `GlassStyles.xaml`, `ToolIcons.xaml`
- `tools/generate-icon.ps1` — генератор многоразрешённого `.ico` (16/32/48/64/128/256)
- `dist/PaintPro.exe` — финальный self-contained single-file билд (~78 МБ)

## Запуск из исходников

```pwsh
cd src/PaintPro.Wpf
dotnet run -c Release
```

Требует **.NET 8 SDK**. Релизный билд — `dotnet build -c Release`.

## Сборка финального .exe

```pwsh
cd src/PaintPro.Wpf
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o ../../dist
```

На выходе — `dist/PaintPro.exe` (~78 МБ, single file, всё внутри).
Для portable-сборки без runtime: убрать `--self-contained true` → ~5 МБ, но нужен .NET 8 Desktop Runtime.

### Иконка

```pwsh
powershell -ExecutionPolicy Bypass -File tools/generate-icon.ps1
```

Перегенерирует `src/PaintPro.Wpf/Assets/AppIcon.ico` (палитра + кисть на градиентном фоне).
csproj уже ссылается на него через `<ApplicationIcon>`.

## Архитектура

См. `REWRITE_PROMPT_CSHARP.md` для полной спеки и обоснования каждого решения
(включая 7 антипаттернов из Electron-версии и как они адресованы в C#).

### Ключевые инварианты

1. **`Selection` ⊕ `FloatingPickup`** — взаимоисключающие. Property-setter
   автоматически коммитит/сбрасывает другой. (см. `Document.cs`)
2. **`ClearCanvasCommand`** сбрасывает все state-слои — не только bitmap, но и
   selection/floating/mode. (антипаттерн §1)
3. **Handles скрываются на время drawing** через `ToolContext.IsDrawing`.
   (антипаттерн §2)
4. **Хоткеи живут на Window.InputBindings**, кнопки — `Focusable="False"`.
   (антипаттерн §3)
5. **Undo через Command Pattern с diff-bitmap**, не PNG-snapshot всего canvas.
   (антипаттерн §5)
6. **Polygon lazy-erase** при первом translate/scale/rotate, по форме
   полигона а не bbox. (антипаттерн §6)
7. **Один `DocumentMode` enum**, не куча bool-флагов. (антипаттерн §7)

### Добавить новый tool

1. Реализовать `ITool` в `Tools/MyTool.cs`.
2. Добавить значение в `ToolKind` enum.
3. Зарегистрировать в `MainViewModel._tools`.
4. Добавить `ToggleButton` в `MainWindow.xaml` (левая панель) + опционально `KeyBinding`.

Всё. Никаких изменений в Document, History, или CanvasView не требуется.

## Хоткеи

| Действие | Клавиши |
|---|---|
| Pencil / Brush / Marker / Eraser | P / B / M / E |
| Fill / Picker / Text | G / I / T |
| Line / Rect / Ellipse | L / R / O |
| Select / Quad / Crop / Hand | S / Q / C / H |
| Undo / Redo | Ctrl+Z / Ctrl+Y |
| New / Open / Save / Save As | Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S |
| Cut / Copy / Paste | Ctrl+X / Ctrl+C / Ctrl+V |
| Select All / Deselect | Ctrl+A / Ctrl+D |
| Delete / Cancel / Commit | Delete / Esc / Enter |
| Zoom in / out / reset | Ctrl+= / Ctrl+- / Ctrl+0 |
| Zoom через колесо | Ctrl + wheel |
| Drag & drop файла | открывает изображение |

## Технологии

- **WPF** (.NET 8.0-windows) — нативный UI, blur/acrylic, hardware-accelerated
- **SkiaSharp 2.88** + **SkiaSharp.Views.WPF** — рендеринг канваса (Chrome's Skia)
- **CommunityToolkit.Mvvm 8.3** — `[ObservableProperty]`/`[RelayCommand]` source generators
- **WPF-UI 3.0** — для acrylic glass-эффектов
