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
        var pickup = _floatingPickup;
        var target = TargetLayer(pickup);

        if (target is not null)
        {
            // Record a before/after diff over the affected region so the commit is undoable.
            // "Before" comes from the pre-lift snapshot when there is one (the layer has
            // already been lazily erased by then); for a paste there is no snapshot and the
            // layer is still untouched, so the current pixels are the correct "before".
            var dirty = ComputeDirtyRect(pickup, target.Width, target.Height);
            SKBitmap? before = null;
            if (!dirty.IsEmpty)
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
                    pickup.CommitLabel, target.Id, dirty, before, after));
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
            if (!source.IsEmpty) BlitRegion(target.Bitmap, snap, source);
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

        if (pickup.OriginalAreaErased && pickup.PreEditSnapshot is { } snap
            && TargetLayer(pickup) is { } target)
        {
            var source = SourceRect(pickup, target.Width, target.Height);
            if (!source.IsEmpty)
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

    /// <summary>Layer a pickup belongs to: the one it was lifted from, falling back to the active layer.</summary>
    private PixelLayer? TargetLayer(FloatingPickup pickup)
        => FindPixelLayer(pickup.SourceLayerId) ?? ActiveLayer as PixelLayer;

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
    public static void DrawPickup(SKCanvas canvas, FloatingPickup pickup)
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
        canvas.DrawBitmap(pickup.SourceBitmap, pickup.CurrentBBox);
        canvas.Restore();
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
