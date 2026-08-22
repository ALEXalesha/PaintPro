using PaintPro.Services;
using SkiaSharp;

namespace PaintPro.Models;

/// <summary>
/// Temporary "lifted" layer used while pixels are being moved, scaled or rotated.
/// Rendered on top of the document during the transformation; on commit its pixels
/// merge back into the active layer.
///
/// Holds two coordinate systems:
///   • <see cref="SourceBitmap"/> + <see cref="OriginalBBox"/> — the captured pixels and where they came from.
///   • <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/<see cref="Height"/>/<see cref="Rotation"/>
///     — the current placement (after the user's drags).
///
/// For polygon (quad) pickups, <see cref="Quad"/> holds 4 corner points in document coords.
/// Content is NOT warped; the quad acts as a <see cref="SKPath"/> clip when rendering.
/// </summary>
public sealed class FloatingPickup : IDisposable
{
    /// <summary>Captured pixels from the active layer at promote time.</summary>
    public SKBitmap SourceBitmap { get; }

    /// <summary>Where the pickup was captured from on the active layer (in document coords).</summary>
    public SKRect OriginalBBox { get; }

    // Current placement (these change as the user drags handles).
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    /// <summary>Rotation in radians around the centre of the current bbox.</summary>
    public float Rotation { get; set; }

    /// <summary>
    /// For polygon pickups: the 4 corners in document coords. Null for rectangular pickups.
    /// Quad corners can be dragged independently; this changes the clip shape, not the pixels.
    /// </summary>
    public SKPoint[]? Quad { get; set; }

    /// <summary>
    /// Snapshot of the polygon shape at the moment the pixels were LIFTED.
    /// Used by the lazy-erase logic so the original area is erased as a polygon,
    /// not as the bbox — see REWRITE_PROMPT_CSHARP.md §"6. Pickup for polygon ...".
    ///
    /// Именно на момент подъёма, а не на момент первого перемещения: перетаскивание угла
    /// меняет маску пикапа, но не то, что было взято из слоя.
    /// </summary>
    public SKPoint[]? OriginalQuad { get; set; }

    /// <summary>True once lazy-erase has run; prevents erasing the source area twice.</summary>
    public bool OriginalAreaErased { get; set; }

    /// <summary>
    /// Запись истории, которая создала этот пикап (вставка), или null, если его поднял
    /// пользователь. Снять такой пикап - дело самой этой записи: убрать его в обход неё
    /// значило бы стереть картинку с холста, оставив «Вставку» текущей, и повтор её уже
    /// не возвращал - курсору некуда двигаться.
    ///
    /// Ссылка, а не флажок: снимать пикап вправе только та запись, которая сейчас
    /// последняя. Между вставкой и удалением успевает лечь другая правка (тот же ползунок
    /// прозрачности слоя), и отмена «текущей» задела бы её, а не вставку.
    /// </summary>
    public Commands.IDocumentCommand? Owner { get; set; }

    /// <summary>Геометрия пикапа на момент подъёма - с чем сравнивать, двигали его или нет.</summary>
    private (float X, float Y, float W, float H, float Rotation, SKPoint[]? Quad)? _origin;

    /// <summary>
    /// Запомнить положение сразу после подъёма. Зовётся один раз, при создании пикапа.
    /// </summary>
    public void RememberOrigin()
        => _origin = (X, Y, Width, Height, Rotation, Quad is { } q ? (SKPoint[])q.Clone() : null);

    /// <summary>
    /// Изменилось ли хоть что-нибудь с момента подъёма. Сравнивается геометрия, а не
    /// выставляется флаг в каждом обработчике перетаскивания: забытый флаг - это молча
    /// потерянная запись в истории, а лишний - запись про то, чего не было.
    ///
    /// Отдельного признака «пиксели новые» нет: он и есть отсутствие снимка слоя. У
    /// вставки поднимать было нечего, и класть обратно тоже нечего - такой пикап
    /// изменяет документ самим фактом своего существования.
    /// Так же устроен <c>floatingMoved</c> в Electron-версии.
    /// </summary>
    public bool HasMoved
    {
        get
        {
            if (PreEditSnapshot is null) return true;
            if (_origin is not { } o) return true;
            if (X != o.X || Y != o.Y || Width != o.W || Height != o.H) return true;
            if (Rotation != o.Rotation) return true;
            if ((Quad is null) != (o.Quad is null)) return true;
            if (Quad is { } cur && o.Quad is { } was)
            {
                for (int i = 0; i < cur.Length && i < was.Length; i++)
                    if (cur[i] != was[i]) return true;
            }
            return false;
        }
    }

    /// <summary>Label the commit gets in the history panel ("Перемещение", "Вставка", …).</summary>
    public string CommitLabel { get; set; } = "Перемещение";

    /// <summary>
    /// Layer the pixels were lifted from. The commit must land back on that layer even if
    /// the user switched the active layer while the pickup was floating.
    /// </summary>
    public Guid SourceLayerId { get; set; }

    /// <summary>
    /// Full active-layer snapshot captured when the pixels were lifted, used to build an
    /// undoable diff when the move/resize/rotate is committed. Null for pickups that don't
    /// record undo (e.g. paste, which carries its own command). Owned by the pickup.
    /// </summary>
    public SKBitmap? PreEditSnapshot { get; set; }

    public FloatingPickup(SKBitmap sourceBitmap, SKRect originalBBox)
    {
        SourceBitmap = sourceBitmap;
        OriginalBBox = originalBBox;
        X = originalBBox.Left;
        Y = originalBBox.Top;
        Width = originalBBox.Width;
        Height = originalBBox.Height;
    }

    public SKRect CurrentBBox => new(X, Y, X + Width, Y + Height);
    public SKPoint Center => new(X + Width / 2f, Y + Height / 2f);

    /// <summary>
    /// Apply an anchor-based resize given which handle the user is dragging
    /// and the current mouse position in world (document) coords.
    /// Re-uses <see cref="GeometryMath.ResizeRotated"/> which handles arbitrary rotation.
    /// </summary>
    public void ApplyResize(ResizeHandle handle, SKPoint mouseWorld)
    {
        var r = GeometryMath.ResizeRotated(X, Y, Width, Height, Rotation, handle, mouseWorld);
        // If we have a quad, scale its corners around the same anchor so the polygon clip
        // tracks the resize. Quad corner-drag is a different op (changes shape, not size).
        //
        // The anchor has to be the WORLD position of the opposite handle, and the scaling
        // has to happen along the pickup's own axes — which is what ResizeRotated does for
        // the bbox. Scaling around the un-rotated local point along world axes instead left
        // the polygon clip somewhere else entirely as soon as the pickup was rotated: the
        // mask slid off the pixels it was supposed to cut. The Electron build already did
        // this the right way; only this side was wrong.
        if (Quad is { } q && Width > 0 && Height > 0)
        {
            float sx = r.Width  / Width;
            float sy = r.Height / Height;
            var anchorWorld = GeometryMath.CornerWorldPosition(
                X, Y, Width, Height, Rotation, GeometryMath.Opposite(handle));
            for (int i = 0; i < 4; i++)
            {
                var rel = new SKPoint(q[i].X - anchorWorld.X, q[i].Y - anchorWorld.Y);
                var local = GeometryMath.Rotate(rel, SKPoint.Empty, -Rotation);
                var scaled = new SKPoint(local.X * sx, local.Y * sy);
                var world = GeometryMath.Rotate(scaled, SKPoint.Empty, Rotation);
                q[i] = new SKPoint(anchorWorld.X + world.X, anchorWorld.Y + world.Y);
            }
        }
        X = r.X; Y = r.Y; Width = r.Width; Height = r.Height;
    }

    /// <summary>Set rotation angle (radians) around the bbox centre.</summary>
    public void SetRotation(float radians) => Rotation = radians;

    public void Dispose()
    {
        SourceBitmap.Dispose();
        PreEditSnapshot?.Dispose();
        PreEditSnapshot = null;
    }
}
