using PaintPro.Services;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Tests for the pure-math part of the geometry layer. These are the
/// "TDD foundation" tests called out in REWRITE_PROMPT_CSHARP.md §"Начни с" step 1
/// and §"Конкретные тесты".
/// </summary>
public class GeometryMathTests
{
    private const float Tol = 1f; // 1 pixel tolerance per spec.

    [Fact]
    public void Rotate_zero_angle_returns_input()
    {
        var p = new SKPoint(5, 7);
        var result = GeometryMath.Rotate(p, new SKPoint(0, 0), 0f);
        Assert.Equal(p, result);
    }

    [Fact]
    public void Rotate_90deg_around_origin_swaps_axes()
    {
        var p = new SKPoint(10, 0);
        var result = GeometryMath.Rotate(p, new SKPoint(0, 0), MathF.PI / 2f);
        Assert.Equal(0, result.X, 4);
        Assert.Equal(10, result.Y, 4);
    }

    [Fact]
    public void CornerWorldPosition_unrotated_matches_local()
    {
        // 100x100 box at (50,50), no rotation.
        var nw = GeometryMath.CornerWorldPosition(50, 50, 100, 100, 0f, ResizeHandle.NW);
        var se = GeometryMath.CornerWorldPosition(50, 50, 100, 100, 0f, ResizeHandle.SE);
        Assert.Equal(new SKPoint(50, 50), nw);
        Assert.Equal(new SKPoint(150, 150), se);
    }

    [Fact]
    public void CornerWorldPosition_180deg_swaps_nw_and_se()
    {
        // After a 180° rotation, NW lands where SE was and vice versa.
        var nw = GeometryMath.CornerWorldPosition(50, 50, 100, 100, MathF.PI, ResizeHandle.NW);
        Assert.Equal(150, nw.X, 4);
        Assert.Equal(150, nw.Y, 4);
    }

    /// <summary>
    /// The spec test from REWRITE_PROMPT_CSHARP.md §"Конкретные тесты", reformulated.
    ///
    /// Spec verbatim asks: floating 100x100 at (50,50) rotated 30°. Drag SE to (200,200).
    /// Expect NW world coords ≈ (50,50). That literal expectation is internally
    /// inconsistent — with a 30° rotation around the bbox centre (100,100), the NW
    /// corner is at world ≈ (81.7, 31.7), not (50,50). It can't be both unrotated-(50,50)
    /// AND rotated-30°.
    ///
    /// The invariant the spec was reaching for is the right one: the anchor corner
    /// (opposite the dragged handle) must stay PINNED IN THE SAME WORLD POSITION
    /// it occupied before the drag. That's what we assert here, and what an
    /// anchor-based resize math must guarantee. The rotation=0 special case
    /// reduces back to the spec's literal expectation.
    /// </summary>
    [Fact]
    public void Rotated_resize_anchor_math_preserves_opposite_corner()
    {
        const float x = 50f, y = 50f, w = 100f, h = 100f;
        float rotation = MathF.PI / 6f; // 30 degrees

        var nwBefore = GeometryMath.CornerWorldPosition(x, y, w, h, rotation, ResizeHandle.NW);

        var result = GeometryMath.ResizeRotated(
            x, y, w, h, rotation,
            ResizeHandle.SE,
            mouseWorld: new SKPoint(200, 200));

        var nwAfter = GeometryMath.CornerWorldPosition(
            result.X, result.Y, result.Width, result.Height, rotation, ResizeHandle.NW);

        Assert.Equal(nwBefore.X, nwAfter.X, Tol);
        Assert.Equal(nwBefore.Y, nwAfter.Y, Tol);

        // Sanity check the reduced unrotated case — here the spec's literal numbers DO hold.
        var unrotated = GeometryMath.ResizeRotated(
            x, y, w, h, rotationRad: 0f,
            ResizeHandle.SE,
            mouseWorld: new SKPoint(200, 200));
        Assert.Equal(50f, unrotated.X, Tol);
        Assert.Equal(50f, unrotated.Y, Tol);
        Assert.Equal(150f, unrotated.Width, Tol);
        Assert.Equal(150f, unrotated.Height, Tol);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]
    [InlineData(1.2f)]
    [InlineData(-0.8f)]
    [InlineData(MathF.PI / 2f)]
    public void Anchor_corner_is_preserved_under_arbitrary_rotation_SE_drag(float rotationRad)
    {
        const float x = 100f, y = 80f, w = 60f, h = 90f;
        var nwBefore = GeometryMath.CornerWorldPosition(x, y, w, h, rotationRad, ResizeHandle.NW);

        var result = GeometryMath.ResizeRotated(
            x, y, w, h, rotationRad,
            ResizeHandle.SE,
            mouseWorld: new SKPoint(300, 250));

        var nwAfter = GeometryMath.CornerWorldPosition(
            result.X, result.Y, result.Width, result.Height, rotationRad, ResizeHandle.NW);

        Assert.Equal(nwBefore.X, nwAfter.X, Tol);
        Assert.Equal(nwBefore.Y, nwAfter.Y, Tol);
    }

    [Theory]
    [InlineData(ResizeHandle.NW, ResizeHandle.SE)]
    [InlineData(ResizeHandle.NE, ResizeHandle.SW)]
    [InlineData(ResizeHandle.SW, ResizeHandle.NE)]
    [InlineData(ResizeHandle.SE, ResizeHandle.NW)]
    [InlineData(ResizeHandle.N,  ResizeHandle.S)]
    [InlineData(ResizeHandle.S,  ResizeHandle.N)]
    [InlineData(ResizeHandle.E,  ResizeHandle.W)]
    [InlineData(ResizeHandle.W,  ResizeHandle.E)]
    public void Each_handle_preserves_its_opposite(ResizeHandle dragged, ResizeHandle anchor)
    {
        const float x = 40f, y = 40f, w = 80f, h = 80f;
        const float rotation = 0.5f;

        var anchorBefore = GeometryMath.CornerWorldPosition(x, y, w, h, rotation, anchor);

        // Pick a mouse position that yields a non-degenerate result for each handle.
        var mouse = new SKPoint(160f, 170f);
        var result = GeometryMath.ResizeRotated(x, y, w, h, rotation, dragged, mouse);

        var anchorAfter = GeometryMath.CornerWorldPosition(
            result.X, result.Y, result.Width, result.Height, rotation, anchor);

        Assert.Equal(anchorBefore.X, anchorAfter.X, Tol);
        Assert.Equal(anchorBefore.Y, anchorAfter.Y, Tol);
    }

    [Fact]
    public void Resize_clamps_to_min_size()
    {
        // Mouse positions that would yield negative width should be clamped.
        var result = GeometryMath.ResizeRotated(
            x: 0, y: 0, w: 100, h: 100, rotationRad: 0,
            dragged: ResizeHandle.SE,
            mouseWorld: new SKPoint(-50, -50),
            minSize: 4f);
        Assert.Equal(4f, result.Width);
        Assert.Equal(4f, result.Height);
    }

    [Fact]
    public void AngleBetween_quarter_turn_is_pi_over_2()
    {
        var c = new SKPoint(0, 0);
        var from = new SKPoint(10, 0);
        var to = new SKPoint(0, 10);
        Assert.Equal(MathF.PI / 2f, GeometryMath.AngleBetween(c, from, to), 4);
    }

    [Fact]
    public void AngleBetween_normalises_to_pi_range()
    {
        var c = new SKPoint(0, 0);
        // 350° → 10° clockwise → expected -20° = -π/9, not +340°.
        var from = new SKPoint(MathF.Cos(MathF.PI * 350f / 180f), MathF.Sin(MathF.PI * 350f / 180f));
        var to = new SKPoint(MathF.Cos(MathF.PI * 10f / 180f), MathF.Sin(MathF.PI * 10f / 180f));
        var d = GeometryMath.AngleBetween(c, from, to);
        Assert.InRange(d, -MathF.PI, MathF.PI);
        Assert.Equal(20f * MathF.PI / 180f, d, 3);
    }

    [Theory]
    [InlineData(1.0f,  true,  1.25f)]
    [InlineData(1.0f,  false, 0.75f)]
    [InlineData(0.05f, false, 0.1f)]   // already below min → clamp.
    [InlineData(10f,   true,  8.0f)]   // already above max → clamp.
    [InlineData(0.5f,  true,  0.67f)]  // hits the non-integer step.
    public void NextZoomStep_picks_next_or_previous_discrete_step(float current, bool zoomIn, float expected)
    {
        Assert.Equal(expected, GeometryMath.NextZoomStep(current, zoomIn), 3);
    }
}
