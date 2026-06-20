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

    [ObservableProperty]
    private DocumentMode _mode = DocumentMode.Idle;

    /// <summary>
    /// Bring <see cref="Mode"/> in line with the actual state of Selection/FloatingPickup.
    /// Called automatically by the property setters; tools can also poke this if they
    /// enter ad-hoc modes (DrawingShape, Cropping).
    /// </summary>
    private void RecomputeMode()
    {
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
    public void EnterTransientMode(DocumentMode mode) => Mode = mode;

    /// <summary>
    /// Merge the floating pickup back into the active layer and dispose it.
    /// Safe no-op if there's no pickup. Does NOT touch History — callers wrap this
    /// in a command if undo is wanted.
    /// </summary>
    public void CommitFloating()
    {
        if (_floatingPickup is null) return;
        var pickup = _floatingPickup;

        if (ActiveLayer is PixelLayer pl)
        {
            // If this pickup was lifted from the layer (carries a snapshot) and actually
            // moved/resized/rotated, record a before/after diff over the affected region so
            // the move is undoable. The dirty region spans source + destination.
            bool recordUndo = pickup.PreEditSnapshot is not null && pickup.OriginalAreaErased;
            SKRectI dirty = default;
            SKBitmap? before = null;
            if (recordUndo)
            {
                dirty = ComputeDirtyRect(pickup, pl.Width, pl.Height);
                if (dirty.IsEmpty) recordUndo = false;
                else before = Crop(pickup.PreEditSnapshot!, dirty);
            }

            using (var canvas = new SKCanvas(pl.Bitmap))
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

            if (recordUndo && before is not null)
            {
                var after = pl.ExtractRegion(dirty);
                History.Push(new Commands.RegionDiffCommand("Перемещение", dirty, before, after));
            }
        }

        pickup.Dispose();
        _floatingPickup = null;
        OnPropertyChanged(nameof(FloatingPickup));
        RecomputeMode();
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
