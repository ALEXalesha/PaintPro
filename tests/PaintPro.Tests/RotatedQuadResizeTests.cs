using PaintPro.Models;
using PaintPro.Services;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Масштабирование полигонального пикапа за ручку рамки.
///
/// Полигон - это клип-маска поверх поднятых пикселей, и он обязан ездить вместе с ними.
/// Маска и рамка хранятся НЕПОВЁРНУТЫМИ: поворот накладывает уже рисование
/// (<see cref="Document.DrawPickup"/> крутит канву, а потом режет по маске), поэтому
/// какие пиксели останутся под маской, решает её положение относительно рамки - и
/// только оно. Значит и пересчёт при масштабировании обязан сохранять именно доли.
///
/// Прежний код пересчитывал углы вокруг МИРОВОГО, то есть уже повёрнутого, положения
/// противоположной ручки, при том что сами углы неповёрнуты: две системы координат
/// смешивались, и у повёрнутого объекта маска на первом же движении ручки уезжала с
/// пикселей, которые должна была вырезать. Без поворота ошибка обнулялась, поэтому
/// разницы и не было видно.
/// </summary>
public class RotatedQuadResizeTests
{
    /// <summary>
    /// Углы рамки пикапа в его собственных (неповёрнутых) координатах, по часовой от
    /// левого верхнего - в той же системе, в которой живёт <see cref="FloatingPickup.Quad"/>.
    /// </summary>
    private static SKPoint[] FrameCorners(FloatingPickup fp) => new[]
    {
        GeometryMath.LocalHandlePosition(fp.X, fp.Y, fp.Width, fp.Height, ResizeHandle.NW),
        GeometryMath.LocalHandlePosition(fp.X, fp.Y, fp.Width, fp.Height, ResizeHandle.NE),
        GeometryMath.LocalHandlePosition(fp.X, fp.Y, fp.Width, fp.Height, ResizeHandle.SE),
        GeometryMath.LocalHandlePosition(fp.X, fp.Y, fp.Width, fp.Height, ResizeHandle.SW),
    };

    private static FloatingPickup MakePickup(float rotation)
    {
        var bmp = new SKBitmap(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul);
        var fp = new FloatingPickup(bmp, new SKRect(100, 100, 200, 200)) { Rotation = rotation };
        fp.Quad = FrameCorners(fp); // маска совпадает с рамкой
        return fp;
    }

    /// <summary>Тянем ручку так, чтобы объект вырос вдвое от противоположного угла.</summary>
    private static void DragHandleToDouble(FloatingPickup fp, ResizeHandle handle)
    {
        var anchor = GeometryMath.CornerWorldPosition(
            fp.X, fp.Y, fp.Width, fp.Height, fp.Rotation, GeometryMath.Opposite(handle));
        var grabbed = GeometryMath.CornerWorldPosition(
            fp.X, fp.Y, fp.Width, fp.Height, fp.Rotation, handle);
        fp.ApplyResize(handle, new SKPoint(
            anchor.X + (grabbed.X - anchor.X) * 2f,
            anchor.Y + (grabbed.Y - anchor.Y) * 2f));
    }

    private static void AssertQuadMatchesFrame(FloatingPickup fp)
    {
        var expected = FrameCorners(fp);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(expected[i].X, fp.Quad![i].X, 2);
            Assert.Equal(expected[i].Y, fp.Quad![i].Y, 2);
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.7853981f)]   // 45°
    [InlineData(1.5707963f)]   // 90°
    [InlineData(-2.0943951f)]  // −120°
    public void Quad_glued_to_the_frame_stays_glued_after_a_corner_resize(float rotation)
    {
        using var fp = MakePickup(rotation);
        DragHandleToDouble(fp, ResizeHandle.SE);
        AssertQuadMatchesFrame(fp);
    }

    [Theory]
    [InlineData(ResizeHandle.NW)]
    [InlineData(ResizeHandle.NE)]
    [InlineData(ResizeHandle.SW)]
    [InlineData(ResizeHandle.E)]
    [InlineData(ResizeHandle.N)]
    public void Every_handle_moves_the_mask_with_the_frame(ResizeHandle handle)
    {
        using var fp = MakePickup(MathF.PI / 5f);
        DragHandleToDouble(fp, handle);
        AssertQuadMatchesFrame(fp);
    }

    /// <summary>
    /// Форма маски, не совпадающая с рамкой, должна масштабироваться так же, как рамка:
    /// треугольник остаётся треугольником в тех же долях от новой рамки.
    /// </summary>
    [Fact]
    public void A_triangle_mask_keeps_its_proportions_inside_the_frame()
    {
        using var fp = MakePickup(MathF.PI / 3f);
        // Схлопываем два угла в один - получается треугольник внутри рамки.
        var corners = FrameCorners(fp);
        fp.Quad = new[] { corners[0], corners[1], corners[2], corners[2] };

        DragHandleToDouble(fp, ResizeHandle.SE);

        var frame = FrameCorners(fp);
        Assert.Equal(frame[0].X, fp.Quad[0].X, 2);
        Assert.Equal(frame[0].Y, fp.Quad[0].Y, 2);
        Assert.Equal(fp.Quad[2].X, fp.Quad[3].X, 2);
        Assert.Equal(fp.Quad[2].Y, fp.Quad[3].Y, 2);
    }
}
