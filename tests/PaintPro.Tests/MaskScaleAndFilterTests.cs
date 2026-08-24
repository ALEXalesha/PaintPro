using PaintPro.Models;
using PaintPro.Services;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Две вещи про то, как поднятый объект переезжает на холст: куда попадает его маска и
/// чем он пересэмплируется.
/// </summary>
public class MaskScaleAndFilterTests
{
    /// <summary>
    /// Доли, которые угол маски занимает внутри габарита. Именно они решают, какие
    /// пиксели останутся под маской: битмап натягивается на габарит, а маска режет
    /// результат - и то и другое в неповёрнутых координатах.
    /// </summary>
    private static SKPoint[] Fractions(FloatingPickup fp)
        => fp.Quad!.Select(q => new SKPoint((q.X - fp.X) / fp.Width, (q.Y - fp.Y) / fp.Height)).ToArray();

    /// <summary>
    /// Тянем ручку так, чтобы объект вырос вдвое от противоположной ручки: мышь ставим
    /// туда, куда уехала бы сама ручка. Считать «габарит в полтора раза больше» нельзя -
    /// повёрнутый прямоугольник другого размера стоит и в другом месте.
    /// </summary>
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

    private static void AssertSameFractions(SKPoint[] before, SKPoint[] after)
    {
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i].X, after[i].X, 2);
            Assert.Equal(before[i].Y, after[i].Y, 2);
        }
    }

    /// <summary>
    /// Повёрнутый объект тянут за угловую ручку. Маска обязана остаться на тех же
    /// пикселях - то есть в тех же долях габарита.
    ///
    /// Прежний пересчёт шёл вокруг МИРОВОГО положения противоположной ручки, уже
    /// повёрнутого, при том что углы маски хранятся неповёрнутыми: доли уезжали с
    /// первого же движения, и из-под маски выползал кусок соседнего рисунка.
    /// </summary>
    [Theory]
    [InlineData(0.3f)]
    [InlineData(0.7f)]
    [InlineData(-1.4f)]
    public void A_rotated_mask_keeps_its_place_inside_the_frame(float rotation)
    {
        var doc = new Document(200, 200);
        PickupOps.PromoteQuad(doc, new[]
        {
            new SKPoint(40, 40), new SKPoint(120, 50),
            new SKPoint(110, 120), new SKPoint(45, 115),
        });
        var fp = doc.FloatingPickup!;
        fp.SetRotation(rotation);

        var before = Fractions(fp);
        DragHandleToDouble(fp, ResizeHandle.SE);

        AssertSameFractions(before, Fractions(fp));
    }

    /// <summary>Без поворота правило то же самое - здесь оно работало и раньше.</summary>
    [Fact]
    public void An_unrotated_mask_keeps_its_place_inside_the_frame()
    {
        var doc = new Document(200, 200);
        PickupOps.PromoteQuad(doc, new[]
        {
            new SKPoint(40, 40), new SKPoint(120, 50),
            new SKPoint(110, 120), new SKPoint(45, 115),
        });
        var fp = doc.FloatingPickup!;

        var before = Fractions(fp);
        fp.ApplyResize(ResizeHandle.SE, new SKPoint(180, 180));

        AssertSameFractions(before, Fractions(fp));
    }

    /// <summary>Боковая ручка тянет одну сторону - доли всё равно те же.</summary>
    [Fact]
    public void A_side_handle_keeps_the_mask_in_place_too()
    {
        var doc = new Document(200, 200);
        PickupOps.PromoteQuad(doc, new[]
        {
            new SKPoint(40, 40), new SKPoint(120, 50),
            new SKPoint(110, 120), new SKPoint(45, 115),
        });
        var fp = doc.FloatingPickup!;
        fp.SetRotation(0.9f);

        var before = Fractions(fp);
        DragHandleToDouble(fp, ResizeHandle.E);

        AssertSameFractions(before, Fractions(fp));
    }

    /// <summary>
    /// Уменьшенный объект обязан сглаживаться. Без фильтрации Skia берёт ближайший
    /// пиксель: уменьшенная вдвое фотография теряла каждую вторую строку и рябила, а
    /// увеличенная шла лесенкой. Сборка на экране и в слое общая, значит и в файл
    /// уходило то же самое.
    /// </summary>
    [Fact]
    public void A_shrunken_pickup_is_resampled_with_filtering()
    {
        var doc = new Document(40, 40);
        var pl = (PixelLayer)doc.Layers[0];
        using (var c = new SKCanvas(pl.Bitmap))
            for (int x = 0; x < 32; x += 2)
                c.DrawRect(new SKRect(x, 0, x + 1, 32), new SKPaint { Color = SKColors.Black });

        PickupOps.PromoteRect(doc, new SKRect(0, 0, 32, 32));
        var fp = doc.FloatingPickup!;
        PickupOps.EnsureLazyErase(doc, fp);
        fp.Width = 8; fp.Height = 8;      // вчетверо меньше
        doc.CommitFloating();

        bool anyGrey = false;
        for (int y = 0; y < 8 && !anyGrey; y++)
            for (int x = 0; x < 8; x++)
                if (pl.Bitmap.GetPixel(x, y).Red is > 20 and < 235) { anyGrey = true; break; }

        Assert.True(anyGrey, "полосы в один пиксель уменьшились без сглаживания");
    }

    /// <summary>
    /// А вот один к одному и без поворота фильтровать нельзя: пиксельный рисунок обязан
    /// переезжать без размытия, иначе перетаскивание выделения само по себе портит
    /// картинку.
    /// </summary>
    [Fact]
    public void A_pickup_moved_one_to_one_is_not_blurred()
    {
        var doc = new Document(40, 40);
        var pl = (PixelLayer)doc.Layers[0];
        using (var c = new SKCanvas(pl.Bitmap))
            for (int x = 0; x < 16; x += 2)
                c.DrawRect(new SKRect(x, 0, x + 1, 16), new SKPaint { Color = SKColors.Black });

        PickupOps.PromoteRect(doc, new SKRect(0, 0, 16, 16));
        var fp = doc.FloatingPickup!;
        PickupOps.EnsureLazyErase(doc, fp);
        PickupOps.Translate(fp, 20, 20);
        doc.CommitFloating();

        for (int x = 0; x < 16; x++)
        {
            var p = pl.Bitmap.GetPixel(20 + x, 24);
            Assert.True(p.Red is < 20 or > 235, $"пиксель {x} размыт: {p}");
        }
    }
}
