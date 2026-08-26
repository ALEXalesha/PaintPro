using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// Handle position around an axis-aligned bounding box. Names follow compass directions:
/// NW = top-left, N = top-centre, NE = top-right, E = right-centre, SE = bottom-right,
/// S = bottom-centre, SW = bottom-left, W = left-centre.
/// </summary>
public enum ResizeHandle
{
    NW, N, NE, E, SE, S, SW, W,
}

/// <summary>
/// Pure-math helpers for canvas geometry. No I/O, no WPF, no Skia state — everything
/// here is unit-testable in isolation. This is the file the spec wants TDD-style tests
/// against (see REWRITE_PROMPT_CSHARP.md §"Начни с" step 1).
/// </summary>
public static class GeometryMath
{
    private const float Epsilon = 1e-6f;

    /// <summary>
    /// Есть ли у прямоугольника площадь, то есть найдётся ли в нём хоть один пиксель.
    ///
    /// Не то же самое, что <see cref="SKRectI.IsEmpty"/>: он отвечает true только на
    /// прямоугольник из четырёх нулей, а полоса нулевой ширины (10,5,10,25) для него
    /// вполне себе непустая. Такие полосы получаются на каждом пересечении с холстом,
    /// когда фигура приткнулась к самому его краю, и дальше из них строился битмап
    /// нулевого размера, на котором Skia падала с ArgumentNullException. Все проверки
    /// «есть ли что обрабатывать» идут теперь через это.
    /// </summary>
    public static bool HasArea(this SKRectI r) => r.Width > 0 && r.Height > 0;

    /// <summary>
    /// Округлить угол до ближайшего кратного <paramref name="step"/>. Shift при повороте:
    /// без него встать ручкой ровно на 90° или 45° нельзя, а поворот на ровный угол -
    /// самое частое, чего от него хотят. В Electron-версии шаг такой же, π/12.
    /// </summary>
    public static float SnapAngle(float radians, float step)
        => step <= 0f ? radians : MathF.Round(radians / step) * step;

    /// <summary>Шаг привязки поворота с зажатым Shift - 15°.</summary>
    public const float RotationSnapStep = MathF.PI / 12f;

    /// <summary>Rotate a point around an arbitrary centre by angle (radians).</summary>
    public static SKPoint Rotate(SKPoint p, SKPoint centre, float angleRad)
    {
        if (Math.Abs(angleRad) < Epsilon) return p;
        float dx = p.X - centre.X;
        float dy = p.Y - centre.Y;
        float c = MathF.Cos(angleRad);
        float s = MathF.Sin(angleRad);
        return new SKPoint(
            centre.X + dx * c - dy * s,
            centre.Y + dx * s + dy * c);
    }

    /// <summary>
    /// World-space position of the named corner of an axis-aligned rectangle (x,y,w,h)
    /// AFTER rotation by <paramref name="rotationRad"/> around its centre.
    /// </summary>
    public static SKPoint CornerWorldPosition(
        float x, float y, float w, float h, float rotationRad, ResizeHandle handle)
    {
        var centre = new SKPoint(x + w / 2f, y + h / 2f);
        var localCorner = LocalHandlePosition(x, y, w, h, handle);
        return Rotate(localCorner, centre, rotationRad);
    }

    /// <summary>
    /// Position of <paramref name="handle"/> in axis-aligned (un-rotated) local coords.
    /// </summary>
    public static SKPoint LocalHandlePosition(float x, float y, float w, float h, ResizeHandle handle)
        => handle switch
        {
            ResizeHandle.NW => new SKPoint(x,         y),
            ResizeHandle.N  => new SKPoint(x + w / 2, y),
            ResizeHandle.NE => new SKPoint(x + w,     y),
            ResizeHandle.E  => new SKPoint(x + w,     y + h / 2),
            ResizeHandle.SE => new SKPoint(x + w,     y + h),
            ResizeHandle.S  => new SKPoint(x + w / 2, y + h),
            ResizeHandle.SW => new SKPoint(x,         y + h),
            ResizeHandle.W  => new SKPoint(x,         y + h / 2),
            _ => throw new ArgumentOutOfRangeException(nameof(handle)),
        };

    /// <summary>Угловая ручка (тянет обе стороны) в отличие от боковой.</summary>
    public static bool IsCorner(ResizeHandle h)
        => h is ResizeHandle.NW or ResizeHandle.NE or ResizeHandle.SE or ResizeHandle.SW;

    /// <summary>Returns the diagonally opposite handle for corner handles, or the opposite side for edge handles.</summary>
    public static ResizeHandle Opposite(ResizeHandle h) => h switch
    {
        ResizeHandle.NW => ResizeHandle.SE,
        ResizeHandle.N  => ResizeHandle.S,
        ResizeHandle.NE => ResizeHandle.SW,
        ResizeHandle.E  => ResizeHandle.W,
        ResizeHandle.SE => ResizeHandle.NW,
        ResizeHandle.S  => ResizeHandle.N,
        ResizeHandle.SW => ResizeHandle.NE,
        ResizeHandle.W  => ResizeHandle.E,
        _ => throw new ArgumentOutOfRangeException(nameof(h)),
    };

    /// <summary>Result of an anchor-based resize calculation in axis-aligned canvas coords.</summary>
    public readonly record struct ResizeResult(float X, float Y, float Width, float Height);

    /// <summary>
    /// Anchor-based resize for a (possibly) rotated rectangle.
    ///
    /// Inputs describe the current placement before the drag:
    ///   (<paramref name="x"/>,<paramref name="y"/>,<paramref name="w"/>,<paramref name="h"/>) — axis-aligned bbox.
    ///   <paramref name="rotationRad"/> — current rotation around the centre of that bbox.
    ///   <paramref name="dragged"/>     — which handle the user is dragging.
    ///   <paramref name="mouseWorld"/>  — current mouse position in world coords.
    ///
    /// The handle <see cref="Opposite(ResizeHandle)"/> stays pinned in world coords.
    /// Width/Height come from the mouse delta in the rectangle's local space.
    /// Returned X/Y position the new axis-aligned bbox so that its rotated anchor corner
    /// lands back on the original anchor world position.
    ///
    /// The rotation itself is unchanged by this function.
    /// </summary>
    /// <param name="keepAspect">
    /// Shift: угловая ручка сохраняет пропорции. Соотношение берётся у текущего габарита -
    /// а он после первого же шага уже пропорционален, так что за время жеста оно не плывёт.
    /// Боковые ручки меняют одну сторону по определению, их это не касается: так же
    /// устроено и в Electron-версии (там условие `h.length === 2`).
    /// </param>
    public static ResizeResult ResizeRotated(
        float x, float y, float w, float h, float rotationRad,
        ResizeHandle dragged, SKPoint mouseWorld,
        float minSize = 4f, bool keepAspect = false)
    {
        var anchorHandle = Opposite(dragged);
        var anchorWorld = CornerWorldPosition(x, y, w, h, rotationRad, anchorHandle);

        // Express mouse in the rectangle's local (un-rotated) frame, with the anchor at origin.
        // Subtract anchor → rotate by −rotationRad.
        var rel = new SKPoint(mouseWorld.X - anchorWorld.X, mouseWorld.Y - anchorWorld.Y);
        var local = Rotate(rel, new SKPoint(0, 0), -rotationRad);

        // Determine new width/height based on which handle is being dragged.
        // For corner handles both dims change; for edge handles only one.
        // Edge handles preserve the OTHER dimension at its previous value.
        float newW = w, newH = h;
        switch (dragged)
        {
            case ResizeHandle.SE: newW =  local.X; newH =  local.Y; break;
            case ResizeHandle.SW: newW = -local.X; newH =  local.Y; break;
            case ResizeHandle.NE: newW =  local.X; newH = -local.Y; break;
            case ResizeHandle.NW: newW = -local.X; newH = -local.Y; break;
            case ResizeHandle.E:  newW =  local.X;                  break;
            case ResizeHandle.W:  newW = -local.X;                  break;
            case ResizeHandle.S:                   newH =  local.Y; break;
            case ResizeHandle.N:                   newH = -local.Y; break;
        }

        // Enforce a minimum so the bbox can't collapse to zero (which would lose the rotation pivot).
        if (newW < minSize) newW = minSize;
        if (newH < minSize) newH = minSize;

        // Пропорции - только у угловых ручек и только после нижней границы: иначе
        // подтянутая до минимума сторона вытягивала бы вторую по старому соотношению.
        if (keepAspect && IsCorner(dragged) && w > 0f && h > 0f)
        {
            float ratio = w / h;
            if (newW / newH > ratio) newW = newH * ratio;
            else                     newH = newW / ratio;
            if (newW < minSize) newW = minSize;
            if (newH < minSize) newH = minSize;
        }

        // The dragged handle's new LOCAL position with respect to the anchor:
        // by construction, the anchor sits at (0,0) and the dragged at (±newW, ±newH).
        // For corner handles: dragged corner local-pos signs equal the signs we used above.
        // We want the world position of the new axis-aligned bbox's top-left (X, Y).
        //
        // Strategy: find the local position of the *centre* of the new rectangle relative to
        // the anchor, rotate by +rotationRad, add anchorWorld, then subtract (newW/2, newH/2)
        // because X/Y is the axis-aligned top-left.
        //
        // The local centre relative to the anchor depends on which corner is anchor:
        var anchorLocal = LocalHandlePosition(0, 0, newW, newH, anchorHandle);
        var centreLocalRelAnchor = new SKPoint(newW / 2f - anchorLocal.X, newH / 2f - anchorLocal.Y);
        var centreWorld = new SKPoint(
            anchorWorld.X + centreLocalRelAnchor.X * MathF.Cos(rotationRad) - centreLocalRelAnchor.Y * MathF.Sin(rotationRad),
            anchorWorld.Y + centreLocalRelAnchor.X * MathF.Sin(rotationRad) + centreLocalRelAnchor.Y * MathF.Cos(rotationRad));

        return new ResizeResult(
            X: centreWorld.X - newW / 2f,
            Y: centreWorld.Y - newH / 2f,
            Width: newW,
            Height: newH);
    }

    /// <summary>
    /// Лежит ли точка внутри многоугольника. Обычный ray casting: считаем, сколько рёбер
    /// пересекает луч вправо от точки; нечётное число - внутри.
    ///
    /// Одна реализация на всех: по своей форме проверяются и выделение-многоугольник, и
    /// поднятый quad. Пока копий было две, одна из них умела только габарит.
    /// </summary>
    public static bool PointInPolygon(IReadOnlyList<SKPoint> corners, SKPoint p)
    {
        bool inside = false;
        for (int i = 0, j = corners.Count - 1; i < corners.Count; j = i++)
        {
            var pi = corners[i];
            var pj = corners[j];
            if (((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// Angle (in radians, range −π..π) between (centre→from) and (centre→to).
    /// Used by the rotate-handle: delta = AngleBetween(prev, current).
    /// </summary>
    public static float AngleBetween(SKPoint centre, SKPoint from, SKPoint to)
    {
        float a1 = MathF.Atan2(from.Y - centre.Y, from.X - centre.X);
        float a2 = MathF.Atan2(to.Y - centre.Y, to.X - centre.X);
        float d = a2 - a1;
        // Normalise to (−π, π].
        while (d <= -MathF.PI) d += 2 * MathF.PI;
        while (d >    MathF.PI) d -= 2 * MathF.PI;
        return d;
    }

    /// <summary>
    /// Snap zoom factor to the nearest discrete step ≥ <paramref name="current"/> when zooming in,
    /// or ≤ when zooming out. Steps from REWRITE_PROMPT_CSHARP.md §"Масштаб (zoom)".
    /// </summary>
    /// <summary>
    /// Ступени масштаба. Наружу - потому что по ним же выбирает потолок, который ставит
    /// размер холста (<see cref="ViewGeometry.LargestAllowedZoom"/>): зажатый масштаб
    /// обязан оставаться на той же лесенке, а не вставать на дробное число между её
    /// ступенями.
    /// </summary>
    public static readonly float[] ZoomSteps =
        { 0.1f, 0.25f, 0.5f, 0.67f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f, 3.0f, 4.0f, 6.0f, 8.0f };

    public static float NextZoomStep(float current, bool zoomIn)
    {
        var steps = ZoomSteps;
        if (zoomIn)
        {
            foreach (var s in steps)
                if (s > current + Epsilon) return s;
            return steps[^1];
        }
        else
        {
            for (int i = steps.Length - 1; i >= 0; i--)
                if (steps[i] < current - Epsilon) return steps[i];
            return steps[0];
        }
    }
}
