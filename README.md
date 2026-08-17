# Paint Pro — WPF + SkiaSharp

Растровый графический редактор в стиле Apple Liquid Glass (iOS 26 / macOS Tahoe 26).
Перенос Electron-версии на C# 12 + WPF + SkiaSharp.

## Что внутри

- `src/PaintPro.Wpf/` — основной WPF-проект
  - `Models/` — `Document`, `Layer`, `Selection`, `FloatingPickup`, `DocumentMode`, `ToolKind`
  - `Commands/` — Command Pattern: `DrawStrokeCommand`, `FillCommand`, `EraseRegionCommand`,
    `RegionDiffCommand`, `ClearCanvasCommand`, `ResizeCanvasCommand`, `PasteCommand`,
    `LayerStackCommand` (добавление / удаление слоя),
    `ReplaceAllLayersCommand` + `DocumentTransform` (поворот / отражение / кадрирование),
    `LayerTarget` (привязка команды к конкретному слою)
  - `Tools/` — `ITool` + 18 реализаций (Pencil, Brush, Marker, Eraser, Fill, Picker, Text,
    Line, Rect, Ellipse, Triangle, Star, Arrow, Heart, Select, Quad, Crop, Hand)
  - `Services/` — `HistoryManager`, `FileService`, `ClipboardService`, `GeometryMath`,
    `BitmapKeying`, `PickupOps` (подъём пикселей и ленивое стирание), `SkiaBitmapBridge`
  - `ViewModels/` — `MainViewModel`, `ColorEntryViewModel`
  - `Views/` — `CanvasView` (SkiaSharp + overlay), `PromptDialog`
  - `Resources/` — `Themes.xaml`, `GlassStyles.xaml`, `ToolIcons.xaml`
- `tools/generate-icon.ps1` — генератор многоразрешённого `.ico` (16/32/48/64/128/256)
- `dist/PaintPro.exe` — финальный self-contained single-file билд (~78 МБ)
- `paint-pro-electron/` — исходная Electron-версия, живёт параллельно и
  поддерживается. Её устройство и грабли: `paint-pro-electron/README.md` и
  `paint-pro-electron/DEV_CONTEXT.md`. Историю обеих версий ведёт общий
  `CHANGELOG.md` (WPF — 1.2.0, Electron — 1.3.0).

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

## Установщик

Inno Setup 6, скрипт — `installer/PaintPro.iss`. Упаковывает готовый
`dist/PaintPro.exe`, поэтому publish должен пройти первым.

```pwsh
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\PaintPro.iss
```

На выходе — `dist/PaintPro-Setup-<версия>.exe`. Версия задаётся в двух местах и
должна совпадать: `MyAppVersion` в `.iss` и `<Version>` в `PaintPro.Wpf.csproj`.

Оба каталога `dist/` в `.gitignore` — бинарники не коммитятся.

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
   полигона а не bbox. (антипаттерн §6). Единственная реализация —
   `PickupOps.EnsureLazyErase`, ей пользуются и SelectTool, и QuadTool, и ручки на канвасе.
7. **Один `DocumentMode` enum**, не куча bool-флагов. (антипаттерн §7).
   `Cropping` и `DrawingShape` задаёт инструмент, `RecomputeMode` их не перетирает.
8. **Команда привязана к слою, а не к «активному сейчас»** — `Layer.Id` переживает
   пересоздание слоя (resize / rotate / crop), команды резолвят его через
   `LayerTarget`. Если слой удалён, undo просто ничего не делает вместо записи
   пикселей в чужой слой.
9. **Операции над документом трогают все слои сразу** — `DocumentTransform` строит
   один `ReplaceAllLayersCommand`. Иначе `CanvasWidth` рассинхронизируется
   с размерами неактивных слоёв.
10. **`Push` игнорируется, пока история применяется** (`HistoryManager.IsApplying`).
    `CommitFloating` умеет писать в историю, и без этого флага вызов изнутри
    `Undo`/`Redo`/`JumpTo` резал список под идущим по нему курсором.
11. **Escape возвращает поднятые пиксели** (`Document.CancelFloating`), Delete
    оставляет дыру, но пишет её в историю (`Document.DiscardFloating`).
12. **Прозрачность применяется один раз, при слиянии** — инструменты рисуют штрих
    непрозрачным в свой буфер, альфа уходит в `DrawStrokeCommand`. Иначе
    перекрытия сегментов внутри одного штриха темнеют.
13. **Стирание вверх по стеку слоёв идёт в прозрачность, а не в белое** —
    `PickupOps.EraseColor` для выделений, `SKBlendMode.DstOut` при слиянии штриха
    ластика. Белым заливается только нижний слой: он и есть бумага документа.
14. **Всё, что видит пользователь, читается с композита, а не с активного слоя** —
    `Document.SampleComposite` для пипетки и статусбара, `FileService.Flatten`
    для копирования в буфер.
15. **История ограничена и по глубине, и по памяти** — `MaxDepth` и `MaxBytes`.
    Команды сообщают свой вес через `IDocumentCommand.ApproximateBytes`; поворот
    большого документа держит два полных битмапа, и одной глубины тут мало.
16. **Обработка мыши только для левой кнопки** — `MouseDown` в WPF приходит для
    любой, без проверки правый клик запускал активный инструмент.
17. **Закрытие окна спрашивает про несохранённое** — `MainViewModel.IsDirty`
    сравнивает позицию в истории с той, что была при последнем сохранении, поэтому
    откат к сохранённому состоянию считается чистым.

### Работа с пикселями напрямую

`SKBitmap.GetPixel`/`SetPixel` пересекают managed/native границу на каждый вызов.
Для попиксельных проходов это доминирующая стоимость: заливка холста 900×600 через
них занимала около 700 мс. `FillCommand` вместо этого копирует сырой BGRA-буфер
через `Marshal.Copy`, работает с `byte[]` и копирует обратно — те же 900×600
укладываются в 25 мс, 4000×3000 в 172 мс.

Буфер премультиплицированный (`Bgra8888`/`Premul`), поэтому цвет заливки нужно
премультиплицировать вручную: `SetPixel` делал это за нас. После записи в нативную
память обязателен `NotifyPixelsChanged()`, иначе Skia может отдать закешированное.

### Форматы файлов

Skia умеет **кодировать только PNG / JPEG / WebP** — `SKImage.Encode` для BMP и GIF
возвращает `null`. Поэтому диалог «Сохранить как» предлагает только эти три, а если
целью оказался другой контейнер (например, открыли `.gif` и нажали Ctrl+S), файл
пишется рядом как `.png` с предупреждением. Открывать при этом можно всё, что
Skia декодирует, включая BMP и GIF.

Запись идёт через `File.Create`, а не `File.OpenWrite`: второй не обрезает файл, и
меньшая картинка поверх большей оставляла бы хвост предыдущей.

### Добавить новый tool

1. Реализовать `ITool` в `Tools/MyTool.cs`.
2. Добавить значение в `ToolKind` enum.
3. Зарегистрировать в `MainViewModel._tools`.
4. Добавить `ToggleButton` в `MainWindow.xaml` (левая панель) + опционально `KeyBinding`.

Всё. Никаких изменений в Document, History, или CanvasView не требуется.

### Выделения

- **Select (S)** — растянуть прямоугольник, клик внутри поднимает пиксели в
  `FloatingPickup`, дальше 8 ручек масштаба и ручка поворота.
- **Quad (Q)** — то же для четырёхугольника. Углы помечены точками и тянутся по
  отдельности: это меняет форму клипа, содержимое не деформируется. Клик внутри
  полигона поднимает пиксели, дальше пикап двигается, масштабируется и вращается
  как прямоугольный. Перетаскивание угла намеренно **не** запускает ленивое
  стирание — исходные пиксели остаются на месте, пока пикап не сдвинут.
- Белый фон вычитается при подъёме (`BitmapKeying`), чтобы выделение не тащило
  за собой непрозрачный белый прямоугольник.

Флажок **«Заливать фигуры»** в правой панели переключает прямоугольник, эллипс,
треугольник, звезду и сердце между контуром и заливкой.

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
