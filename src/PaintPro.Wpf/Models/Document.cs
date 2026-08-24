using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PaintPro.Commands;
using PaintPro.Services;
using SkiaSharp;

namespace PaintPro.Models;

/// <summary>
/// Root model: a stack of layers + a single optional selection + a single optional floating pickup
/// + the undo/redo history.
///
/// Invariant: <see cref="Selection"/> and <see cref="FloatingPickup"/> are mutually exclusive.
/// Setting one auto-clears the other (see REWRITE_PROMPT_CSHARP.md §4).
/// </summary>
public partial class Document : ObservableObject
{
    public Document(int width, int height)
    {
        CanvasWidth = width;
        CanvasHeight = height;
        Layers = new ObservableCollection<Layer>
        {
            new PixelLayer(width, height, SKColors.White) { Name = "Background" },
        };
        ActiveLayerIndex = 0;
        History = new HistoryManager();
    }

    public ObservableCollection<Layer> Layers { get; }

    [ObservableProperty]
    private int _activeLayerIndex;

    /// <summary>
    /// The layer that drawing tools currently mutate. Always points at a valid layer
    /// (clamped if the user deletes the active one).
    /// </summary>
    public Layer ActiveLayer => Layers[Math.Clamp(ActiveLayerIndex, 0, Layers.Count - 1)];

    /// <summary>
    /// Find a pixel layer by its stable id, or null if it no longer exists (the user
    /// deleted it). Commands resolve their target through this so an undo never writes
    /// into a layer it was not recorded against.
    /// </summary>
    public PixelLayer? FindPixelLayer(Guid id)
    {
        foreach (var l in Layers)
            if (l.Id == id && l is PixelLayer pl) return pl;
        return null;
    }

    [ObservableProperty]
    private int _canvasWidth;

    [ObservableProperty]
    private int _canvasHeight;

    public HistoryManager History { get; }

    private Selection? _selection;
    /// <summary>
    /// Current selection (rect or polygon) or null. Setting a non-null value while a
    /// FloatingPickup exists commits the floating pickup first — see antipattern #4.
    /// </summary>
    public Selection? Selection
    {
        get => _selection;
        set
        {
            if (value is not null && _floatingPickup is not null)
            {
                // Setting a selection while a pickup is live → commit the pickup first.
                CommitFloating();
            }
            if (SetProperty(ref _selection, value))
            {
                RecomputeMode();
            }
        }
    }

    private FloatingPickup? _floatingPickup;
    /// <summary>
    /// Temporary edit layer for move/scale/rotate. Setting non-null auto-clears Selection.
    /// </summary>
    public FloatingPickup? FloatingPickup
    {
        get => _floatingPickup;
        set
        {
            if (value is not null && _selection is not null)
            {
                // Lifting pixels into a pickup → the selection that drove it is consumed.
                _selection = null;
                OnPropertyChanged(nameof(Selection));
            }
            if (SetProperty(ref _floatingPickup, value))
            {
                RecomputeMode();
            }
        }
    }

    /// <summary>
    /// Raise a change for Selection / FloatingPickup after mutating one in place.
    /// Dragging a quad corner edits the existing object, so the property setters never
    /// fire and the overlay would keep drawing the old shape.
    /// </summary>
    public void NotifySelectionChanged() => OnPropertyChanged(nameof(Selection));
    public void NotifyFloatingChanged() => OnPropertyChanged(nameof(FloatingPickup));

    [ObservableProperty]
    private DocumentMode _mode = DocumentMode.Idle;

    /// <summary>
    /// Bring <see cref="Mode"/> in line with the actual state of Selection/FloatingPickup.
    /// Called automatically by the property setters; tools can also poke this if they
    /// enter ad-hoc modes (DrawingShape, Cropping).
    /// </summary>
    private void RecomputeMode()
    {
        // Cropping / DrawingShape are driven by the tool, not by the selection state.
        // Without this guard CropTool's own Selection updates immediately knock the
        // document back to SelectionRect.
        if (Mode is DocumentMode.Cropping or DocumentMode.DrawingShape) return;

        Mode = (_selection, _floatingPickup) switch
        {
            (_, not null)            => DocumentMode.FloatingActive,
            (RectSelection,    null) => DocumentMode.SelectionRect,
            (PolygonSelection, null) => DocumentMode.SelectionPolygon,
            _                        => DocumentMode.Idle,
        };
    }

    /// <summary>
    /// Set Mode directly for ad-hoc modes (DrawingShape while a shape tool is dragging,
    /// Cropping while CropTool is active). Caller is responsible for switching back.
    /// </summary>
    public void EnterTransientMode(DocumentMode mode)
    {
        Mode = mode;
        // Leaving a transient mode: fall back to whatever the selection state implies.
        if (mode is not (DocumentMode.Cropping or DocumentMode.DrawingShape)) RecomputeMode();
    }

    /// <summary>
    /// Merge the floating pickup back into the active layer and dispose it.
    /// Safe no-op if there's no pickup. Does NOT touch History — callers wrap this
    /// in a command if undo is wanted.
    /// </summary>
    public void CommitFloating()
    {
        if (_floatingPickup is null) return;

        // Прогулка по ленте истории: пиксели описывают сами записи, и дорисовывать к ним
        // что-то ещё нельзя. History.Push в этот момент молчит (см. HistoryManager.Push),
        // так что нарисованное осталось бы на холсте вовсе без записи о себе - отменить
        // его было бы нечем. Объект, доживший до этого места, создала вставка, а её
        // прижатие, если оно было, лежит в ленте отдельной записью и снимет его само.
        if (History.IsApplying) { DropFloating(); return; }

        var pickup = _floatingPickup;

        // Подняли и положили обратно, ничего не изменив: клик внутрь рамки, нажатие на
        // ручку без перетаскивания. Холст обязан остаться прежним до пикселя, а записи в
        // истории и признаку несохранённой работы такому событию взяться неоткуда.
        // Возврат делает CancelFloating - он же кладёт назад пиксели, если исходную
        // область успели стереть.
        if (!pickup.HasMoved) { CancelFloating(); return; }

        var target = TargetLayer(pickup);

        if (target is not null)
        {
            // Исходная область стирается лениво, при первом перемещении. Если двигали
            // только углы quad'а, стирание ещё не проходило: без него исходный полигон
            // остался бы лежать под прижатым пикапом, а в историю не попало бы ничего.
            // В Electron-версии то же самое делает ensureOriginalQuadErased.
            Services.PickupOps.EnsureLazyErase(this, pickup);

            // Record a before/after diff over the affected region so the commit is undoable.
            // "Before" comes from the pre-lift snapshot when there is one (the layer has
            // already been lazily erased by then); for a paste there is no snapshot and the
            // layer is still untouched, so the current pixels are the correct "before".
            var dirty = ComputeDirtyRect(pickup, target.Width, target.Height);
            SKBitmap? before = null;
            if (dirty.HasArea())
            {
                before = pickup.PreEditSnapshot is { } snap
                    ? Crop(snap, dirty)
                    : target.ExtractRegion(dirty);
            }

            DrawPickup(target.Bitmap, pickup);

            if (before is not null)
            {
                var after = target.ExtractRegion(dirty);
                History.Push(new Commands.RegionDiffCommand(
                    pickup.CommitLabel, target.Id, dirty, before, after, dropsFloating: true));
            }
        }

        pickup.Dispose();
        _floatingPickup = null;
        OnPropertyChanged(nameof(FloatingPickup));
        RecomputeMode();
    }

    /// <summary>
    /// Drop the pickup and put the lifted pixels back where they came from.
    /// This is Escape: nothing about the document should have changed afterwards, so the
    /// lazily-erased source area is restored from the pre-lift snapshot and nothing is
    /// recorded in history.
    /// </summary>
    public void CancelFloating()
    {
        if (_floatingPickup is null) return;
        var pickup = _floatingPickup;

        if (pickup.OriginalAreaErased && pickup.PreEditSnapshot is { } snap
            && TargetLayer(pickup) is { } target)
        {
            var source = SourceRect(pickup, target.Width, target.Height);
            if (source.HasArea()) BlitRegion(target.Bitmap, snap, source);
        }

        pickup.Dispose();
        _floatingPickup = null;
        OnPropertyChanged(nameof(FloatingPickup));
        RecomputeMode();
    }

    /// <summary>
    /// Drop the pickup and keep the hole: this is Delete on a lifted selection.
    /// Unlike <see cref="CancelFloating"/> the erase is intentional, so it goes into
    /// history as a normal diff.
    /// </summary>
    public void DiscardFloating()
    {
        if (_floatingPickup is null) return;
        var pickup = _floatingPickup;

        // Исходную область могли ещё не стереть: подъём её не трогает, стирание идёт при
        // первом перемещении. Пока этого вызова не было, Delete и Ctrl+X по только что
        // поднятому выделению просто снимали пикап - пиксели возвращались на место, и
        // пользователь получал «вырезал, а ничего не вырезалось».
        Services.PickupOps.EnsureLazyErase(this, pickup);

        if (pickup.PreEditSnapshot is { } snap && TargetLayer(pickup) is { } target)
        {
            var source = SourceRect(pickup, target.Width, target.Height);
            if (source.HasArea())
            {
                var before = Crop(snap, source);
                var after = target.ExtractRegion(source);
                History.Push(new Commands.RegionDiffCommand(
                    "Удаление выделения", target.Id, source, before, after));
            }
        }

        pickup.Dispose();
        _floatingPickup = null;
        OnPropertyChanged(nameof(FloatingPickup));
        RecomputeMode();
    }

    /// <summary>
    /// Снять плавающий объект, ничего не возвращая на холст и ничего не записывая.
    ///
    /// Для операций, которые заменяют содержимое всех слоёв разом и меняют размер холста -
    /// поворот, отражение, кадрирование, открытие файла, смена размера. Возвращать
    /// поднятые пиксели там некуда: слои после такой операции другие, а координаты объекта
    /// указывают на холст, которого больше нет. Рамка при этом снимается по той же причине
    /// и в том же месте.
    ///
    /// Отличается от <see cref="CancelFloating"/> тем, что не трогает пиксели, и от
    /// <see cref="CommitFloating"/> - тем, что не пишет в историю: прижимать объект должен
    /// тот, кто затевает операцию, и ДО неё, пока холст ещё прежний.
    /// </summary>
    public void DropFloating()
    {
        if (_floatingPickup is null) return;
        _floatingPickup.Dispose();
        _floatingPickup = null;
        OnPropertyChanged(nameof(FloatingPickup));
        RecomputeMode();
    }

    /// <summary>Layer a pickup belongs to: the one it was lifted from, falling back to the active layer.</summary>
    private PixelLayer? TargetLayer(FloatingPickup pickup)
        => FindPixelLayer(pickup.SourceLayerId) ?? ActiveLayer as PixelLayer;

    /// <summary>
    /// Colour of one pixel as the user sees it: white paper with every visible layer
    /// composited over it. The eyedropper and the status bar both need this — reading the
    /// active layer alone reports transparent wherever that layer happens to be empty.
    ///
    /// Плавающий объект входит в выборку наравне со слоями и на своём месте в стопке -
    /// том же, куда его кладёт <see cref="Render"/>. Пока он в счёт не шёл, пипетка и
    /// статусбар отвечали цветом того, что лежит ПОД вставленной картинкой: на экране
    /// синее, в подсказке белое, а по клику пипеткой в палитру уходило белое. В
    /// Electron-версии пипетка сэмплит композит с floating с самого начала.
    /// </summary>
    public SKColor SampleComposite(int x, int y)
    {
        if (x < 0 || y < 0 || x >= CanvasWidth || y >= CanvasHeight) return SKColors.Transparent;

        var pickup = _floatingPickup;
        bool pickupSampled = false;

        float r = 255f, g = 255f, b = 255f; // start from the white the canvas is cleared to
        void Over(SKColor px, float layerOpacity)
        {
            float a = px.Alpha / 255f * Math.Clamp(layerOpacity, 0f, 1f);
            if (a <= 0f) return;
            r = px.Red * a + r * (1 - a);
            g = px.Green * a + g * (1 - a);
            b = px.Blue * a + b * (1 - a);
        }

        foreach (var layer in Layers)
        {
            if (layer is PixelLayer pl && pl.Visible && x < pl.Width && y < pl.Height)
                Over(pl.Bitmap.GetPixel(x, y), pl.Opacity);

            // Объект ложится поверх своего слоя, но под теми, что выше, - как на экране.
            if (pickup is not null && layer.Id == pickup.SourceLayerId)
            {
                if (SamplePickup(pickup, x, y) is { } fp) Over(fp, layer.Opacity);
                pickupSampled = true;
            }
        }

        // Слоя-источника уже нет - объект рисуется поверх всего, значит и берётся оттуда.
        if (pickup is not null && !pickupSampled && SamplePickup(pickup, x, y) is { } top)
            Over(top, 1f);

        return new SKColor((byte)MathF.Round(r), (byte)MathF.Round(g), (byte)MathF.Round(b));
    }

    /// <summary>
    /// Цвет плавающего объекта в точке документа, или null, если объект её не закрывает.
    ///
    /// Повторяет геометрию <see cref="DrawPickup"/> задом наперёд: поворот снимается с
    /// точки (объект хранит габарит и quad неповёрнутыми), quad работает маской, а внутри
    /// габарита точка пересчитывается в координаты исходного битмапа - объект бывает
    /// растянут.
    /// </summary>
    private static SKColor? SamplePickup(FloatingPickup pickup, int x, int y)
    {
        var point = new SKPoint(x + 0.5f, y + 0.5f);
        var local = Services.PickupOps.ToLocal(pickup, point);

        if (pickup.Quad is { } quad && !Services.GeometryMath.PointInPolygon(quad, local)) return null;

        var box = pickup.CurrentBBox;
        if (box.Width <= 0 || box.Height <= 0) return null;
        if (!box.Contains(local)) return null;

        var bmp = pickup.SourceBitmap;
        int sx = (int)((local.X - box.Left) / box.Width * bmp.Width);
        int sy = (int)((local.Y - box.Top) / box.Height * bmp.Height);
        if (sx < 0 || sy < 0 || sx >= bmp.Width || sy >= bmp.Height) return null;
        return bmp.GetPixel(sx, sy);
    }

    /// <summary>
    /// Собрать документ на канве: слои снизу вверх, а превью инструмента и плавающий
    /// объект - на своём слое, а не поверх всех.
    ///
    /// Пока и то и другое рисовалось последним, штрих по нижнему слою во время
    /// перетаскивания лежал поверх верхних, а на отпускании нырял под них: картинка
    /// прыгала в момент, когда пользователь уже отвёл руку. Через эту же сборку идёт
    /// <see cref="Services.FileService.Flatten"/>, чтобы сохранённый файл и экран не
    /// расходились.
    /// </summary>
    /// <param name="preview">Превью активного инструмента или null.</param>
    /// <param name="previewAlpha">Прозрачность, с которой превью ляжет на слой.</param>
    /// <param name="previewBlend">
    /// Режим, которым превью сольётся со слоем. Ластик на верхнем слое вычитает пиксели
    /// (<see cref="SKBlendMode.DstOut"/>), а не красит белым, и показывать это надо тем же
    /// режимом, каким оно потом ляжет: пока превью рисовалось поверх слоя обычным
    /// source-over, ластик вёл по верхнему слою белую полосу, а на отпускании она
    /// превращалась в дыру с нижним слоем внутри - картинка менялась в момент, когда
    /// пользователь уже отвёл руку.
    /// </param>
    public void Render(SKCanvas canvas, SKBitmap? preview = null, byte previewAlpha = 255,
                       SKFilterQuality quality = SKFilterQuality.Low,
                       SKBlendMode previewBlend = SKBlendMode.SrcOver)
    {
        using var paint = new SKPaint { FilterQuality = quality };
        var pickup = _floatingPickup;
        bool pickupDrawn = false;
        int activeIndex = Math.Clamp(ActiveLayerIndex, 0, Layers.Count - 1);

        for (int i = 0; i < Layers.Count; i++)
        {
            var layer = Layers[i];
            float opacity = Math.Clamp(layer.Opacity, 0f, 1f);
            bool previewHere = preview is not null && i == activeIndex;

            // Вычитающее превью обязано видеть только свой слой. Нарисованное прямо на
            // канве, оно снимало бы и всё, что уже сложено ниже, - ластик на верхнем слое
            // пробивал бы дыру до самой бумаги. Отдельный слой Skia замыкает вычитание на
            // тех пикселях, которых оно и касается на самом деле.
            bool isolate = previewHere && previewBlend != SKBlendMode.SrcOver;
            if (isolate)
            {
                using var groupPaint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * opacity)) };
                canvas.SaveLayer(groupPaint);
            }

            if (layer is PixelLayer pl)
            {
                if (pl.Visible)
                {
                    // Внутри отдельного слоя прозрачность уже учтена в его собственной
                    // краске: применить её второй раз значило бы возвести в квадрат.
                    paint.Color = SKColors.White.WithAlpha((byte)(255 * (isolate ? 1f : opacity)));
                    canvas.DrawBitmap(pl.Bitmap, 0, 0, paint);
                }
            }
            else layer.Render(canvas);

            // Превью ложится на активный слой, поэтому и показывать его надо там же и с
            // прозрачностью этого слоя: иначе на полупрозрачном слое штрих во время
            // рисования темнее, чем окажется после.
            if (previewHere)
            {
                paint.Color = SKColors.White.WithAlpha((byte)(previewAlpha * (isolate ? 1f : opacity)));
                paint.BlendMode = previewBlend;
                canvas.DrawBitmap(preview, 0, 0, paint);
                paint.BlendMode = SKBlendMode.SrcOver;
            }

            if (isolate) canvas.Restore();

            // Плавающий объект - будущее содержимое того слоя, с которого его подняли.
            // Видимость слоя ему не указ: он ещё не его часть, а спрятать то, что
            // пользователь сейчас тащит, хуже любой нестыковки.
            if (pickup is not null && layer.Id == pickup.SourceLayerId)
            {
                DrawPickup(canvas, pickup, (byte)(255 * opacity));
                pickupDrawn = true;
            }
        }

        // Слоя-источника уже нет (его удалили, пока объект висел) - кладём сверху.
        if (pickup is not null && !pickupDrawn) DrawPickup(canvas, pickup);
    }

    /// <summary>Composite a pickup (rotation + optional quad clip) onto a bitmap.</summary>
    public static void DrawPickup(SKBitmap destination, FloatingPickup pickup)
    {
        using var canvas = new SKCanvas(destination);
        DrawPickup(canvas, pickup);
    }

    /// <summary>
    /// Composite a pickup onto an existing canvas, respecting whatever transform is
    /// already on it. The live view and the flattened output must draw the pickup exactly
    /// the same way, so both go through here.
    /// </summary>
    public static void DrawPickup(SKCanvas canvas, FloatingPickup pickup, byte alpha = 255)
    {
        canvas.Save();
        if (pickup.Rotation != 0f)
        {
            var c = pickup.Center;
            canvas.Translate(c.X, c.Y);
            canvas.RotateRadians(pickup.Rotation);
            canvas.Translate(-c.X, -c.Y);
        }
        if (pickup.Quad is { } q)
        {
            using var clipPath = new SKPath();
            clipPath.MoveTo(q[0]);
            clipPath.LineTo(q[1]);
            clipPath.LineTo(q[2]);
            clipPath.LineTo(q[3]);
            clipPath.Close();
            canvas.ClipPath(clipPath, antialias: true);
        }
        using (var paint = alpha == 255 ? null : new SKPaint { Color = SKColors.White.WithAlpha(alpha) })
            canvas.DrawBitmap(pickup.SourceBitmap, pickup.CurrentBBox, paint);
        canvas.Restore();
    }

    /// <summary>
    /// Габарит того, что pickup рисует СЕЙЧАС, в координатах документа: с учётом
    /// поворота (углы вылезают за bbox) и quad-клипа (он, наоборот, бывает уже).
    /// Нужен копированию - копировать по CurrentBBox значило бы срезать углы
    /// повёрнутого объекта.
    /// </summary>
    public static SKRectI PickupBounds(FloatingPickup pickup, int canvasW, int canvasH)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        void Add(SKPoint p)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }

        if (pickup.Quad is { } q)
        {
            // Углы quad'а хранятся неповёрнутыми, а рисуется он вместе с поворотом:
            // копирование по неповёрнутым координатам брало область, в которой объекта
            // уже нет. Повёрнутый габарит - ровно тот, что и у прямоугольного пикапа ниже.
            var qc = pickup.Center;
            foreach (var p in q) Add(pickup.Rotation == 0f ? p : Services.GeometryMath.Rotate(p, qc, pickup.Rotation));
        }
        else
        {
            var bb = pickup.CurrentBBox;
            var corners = new[]
            {
                new SKPoint(bb.Left, bb.Top), new SKPoint(bb.Right, bb.Top),
                new SKPoint(bb.Right, bb.Bottom), new SKPoint(bb.Left, bb.Bottom),
            };
            var center = pickup.Center;
            foreach (var c in corners)
                Add(pickup.Rotation == 0f ? c : Services.GeometryMath.Rotate(c, center, pickup.Rotation));
        }

        var r = new SKRectI(
            (int)MathF.Floor(minX), (int)MathF.Floor(minY),
            (int)MathF.Ceiling(maxX), (int)MathF.Ceiling(maxY));
        return SKRectI.Intersect(r, new SKRectI(0, 0, canvasW, canvasH));
    }

    /// <summary>Area the pickup was lifted from, clamped to the layer.</summary>
    private static SKRectI SourceRect(FloatingPickup pickup, int layerW, int layerH)
    {
        var bounds = pickup.OriginalBBox;
        if (pickup.OriginalQuad is { } oq)
        {
            float minX = oq[0].X, maxX = oq[0].X, minY = oq[0].Y, maxY = oq[0].Y;
            foreach (var p in oq)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            }
            bounds = new SKRect(minX, minY, maxX, maxY);
        }
        // Round outward by one pixel: the erase is antialiased and bleeds past the exact edge.
        var r = new SKRectI(
            (int)MathF.Floor(bounds.Left) - 1, (int)MathF.Floor(bounds.Top) - 1,
            (int)MathF.Ceiling(bounds.Right) + 1, (int)MathF.Ceiling(bounds.Bottom) + 1);
        return SKRectI.Intersect(r, new SKRectI(0, 0, layerW, layerH));
    }

    /// <summary>Copy <paramref name="region"/> out of a full-layer snapshot back onto the layer.</summary>
    private static void BlitRegion(SKBitmap destination, SKBitmap fullSnapshot, SKRectI region)
    {
        using var canvas = new SKCanvas(destination);
        canvas.Save();
        canvas.ClipRect(new SKRect(region.Left, region.Top, region.Right, region.Bottom));
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(fullSnapshot,
            source: new SKRect(region.Left, region.Top, region.Right, region.Bottom),
            dest:   new SKRect(region.Left, region.Top, region.Right, region.Bottom));
        canvas.Restore();
    }

    /// <summary>
    /// Bounding rectangle (clamped to the canvas) that covers both where a pickup was lifted
    /// from and where it ends up — the full set of pixels a commit can change.
    /// </summary>
    private static SKRectI ComputeDirtyRect(FloatingPickup pickup, int canvasW, int canvasH)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        void Add(SKPoint p)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }
        void AddRect(SKRect r) { Add(new SKPoint(r.Left, r.Top)); Add(new SKPoint(r.Right, r.Bottom)); }

        // Source: original quad if present, else original bbox.
        if (pickup.OriginalQuad is { } oq) foreach (var p in oq) Add(p);
        else AddRect(pickup.OriginalBBox);

        // Destination: current bbox corners, rotated about the centre if needed.
        var bb = pickup.CurrentBBox;
        var corners = new[]
        {
            new SKPoint(bb.Left, bb.Top), new SKPoint(bb.Right, bb.Top),
            new SKPoint(bb.Right, bb.Bottom), new SKPoint(bb.Left, bb.Bottom),
        };
        var center = pickup.Center;
        foreach (var c in corners)
            Add(pickup.Rotation == 0f ? c : Services.GeometryMath.Rotate(c, center, pickup.Rotation));
        if (pickup.Quad is { } cq) foreach (var p in cq) Add(p);

        // Round outward and clamp to the canvas.
        int left = Math.Max(0, (int)MathF.Floor(minX));
        int top = Math.Max(0, (int)MathF.Floor(minY));
        int right = Math.Min(canvasW, (int)MathF.Ceiling(maxX));
        int bottom = Math.Min(canvasH, (int)MathF.Ceiling(maxY));
        if (right <= left || bottom <= top) return SKRectI.Empty;
        return new SKRectI(left, top, right, bottom);
    }

    private static SKBitmap Crop(SKBitmap src, SKRectI r)
    {
        var dst = new SKBitmap(r.Width, r.Height, src.ColorType, src.AlphaType);
        using var c = new SKCanvas(dst);
        c.DrawBitmap(src,
            source: new SKRect(r.Left, r.Top, r.Right, r.Bottom),
            dest: new SKRect(0, 0, r.Width, r.Height));
        return dst;
    }
}
