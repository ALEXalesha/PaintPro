using SkiaSharp;

namespace PaintPro.Models;

/// <summary>
/// Represents either a rectangular selection or a 4-point polygon (quad) selection.
/// Discriminated union via subclasses; the Document holds at most one Selection at a time.
/// </summary>
public abstract class Selection
{
    /// <summary>
    /// Axis-aligned bounding box of the selection in canvas (document) coordinates.
    /// Used for handle layout and quick hit-tests before doing detailed polygon math.
    /// </summary>
    public abstract SKRect BoundingBox { get; }

    /// <summary>True if (x,y) in document coords is inside the selection.</summary>
    public abstract bool Contains(SKPoint p);
}

/// <summary>Rectangular selection. Created by the SelectTool (S).</summary>
public sealed class RectSelection : Selection
{
    public SKRect Rect { get; }

    public RectSelection(SKRect rect) => Rect = rect;
    public RectSelection(float x, float y, float w, float h) : this(new SKRect(x, y, x + w, y + h)) { }

    public override SKRect BoundingBox => Rect;
    public override bool Contains(SKPoint p) => Rect.Contains(p);
}

/// <summary>
/// 4-point polygon selection. Created by the QuadTool (Q).
/// Corners can be dragged independently to change the clip shape — content is NOT warped.
/// </summary>
public sealed class PolygonSelection : Selection
{
    private readonly SKPoint[] _corners;

    public PolygonSelection(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3)
    {
        _corners = new[] { p0, p1, p2, p3 };
    }

    public IReadOnlyList<SKPoint> Corners => _corners;

    /// <summary>Replace corner at index 0..3. Used by QuadTool when dragging a single corner.</summary>
    public void SetCorner(int index, SKPoint point) => _corners[index] = point;

    public override SKRect BoundingBox
    {
        get
        {
            float minX = _corners[0].X, maxX = _corners[0].X;
            float minY = _corners[0].Y, maxY = _corners[0].Y;
            for (int i = 1; i < 4; i++)
            {
                if (_corners[i].X < minX) minX = _corners[i].X;
                if (_corners[i].X > maxX) maxX = _corners[i].X;
                if (_corners[i].Y < minY) minY = _corners[i].Y;
                if (_corners[i].Y > maxY) maxY = _corners[i].Y;
            }
            return new SKRect(minX, minY, maxX, maxY);
        }
    }

    public override bool Contains(SKPoint p)
    {
        // Standard ray-casting point-in-polygon for n=4.
        bool inside = false;
        for (int i = 0, j = 3; i < 4; j = i++)
        {
            var pi = _corners[i];
            var pj = _corners[j];
            if (((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X))
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
