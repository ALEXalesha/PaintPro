using System.IO;
using PaintPro.Models;
using PaintPro.Services;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Куда уходит Ctrl+S и что попадает в буфер при копировании плавающего объекта.
/// Обе темы про молчаливую потерю данных: запись не в тот файл и копия не того куска.
/// </summary>
public class FileTargetAndPickupBoundsTests
{
    private static string WritePng(SKColor fill, int w = 8, int h = 8)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(bmp)) c.Clear(fill);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var s = File.Create(path);
        data.SaveTo(s);
        return path;
    }

    [Fact]
    public void Opening_a_file_makes_it_the_save_target()
    {
        var saved = WritePng(SKColors.Red);
        var opened = WritePng(SKColors.Blue);
        try
        {
            var fs = new FileService();
            var doc = new Document(8, 8);

            // Как будто пользователь уже сохранялся в первый файл.
            using (var first = fs.OpenImage(saved)!) { }
            fs.SaveOrSaveAs(doc);
            Assert.Equal(saved, fs.LastSavedPath);

            // Теперь открывает второй — Ctrl+S должен уйти в него, а не в первый.
            using var bmp = fs.OpenImage(opened)!;
            Assert.Equal(opened, fs.LastOpenedPath);

            var outcome = fs.SaveOrSaveAs(doc);
            Assert.Equal(SaveStatus.Ok, outcome.Status);
            Assert.Equal(opened, outcome.Path);
        }
        finally
        {
            File.Delete(saved);
            File.Delete(opened);
        }
    }

    [Fact]
    public void Detach_forces_the_next_save_to_ask_for_a_path()
    {
        var path = WritePng(SKColors.Red);
        try
        {
            var fs = new FileService();
            using var bmp = fs.OpenImage(path)!;
            Assert.NotNull(fs.LastOpenedPath);

            fs.Detach();

            Assert.Null(fs.LastOpenedPath);
            Assert.Null(fs.LastSavedPath);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Pickup_bounds_cover_the_corners_of_a_rotated_object()
    {
        var bmp = new SKBitmap(20, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pickup = new FloatingPickup(bmp, new SKRect(40, 45, 60, 55))
        {
            Rotation = MathF.PI / 2f,   // повёрнут на 90° — габарит меняет стороны местами
        };

        var b = Document.PickupBounds(pickup, 200, 200);

        // Центр (50,50), полудиагональ по осям после поворота: 5 по X, 10 по Y.
        Assert.Equal(45, b.Left);
        Assert.Equal(40, b.Top);
        Assert.Equal(55, b.Right);
        Assert.Equal(60, b.Bottom);
    }

    [Fact]
    public void Pickup_bounds_follow_the_quad_not_the_bbox()
    {
        var bmp = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pickup = new FloatingPickup(bmp, new SKRect(0, 0, 40, 40))
        {
            Quad = new[]
            {
                new SKPoint(10, 10), new SKPoint(30, 12),
                new SKPoint(28, 25), new SKPoint(12, 22),
            },
        };

        var b = Document.PickupBounds(pickup, 100, 100);

        Assert.Equal(10, b.Left);
        Assert.Equal(10, b.Top);
        Assert.Equal(30, b.Right);
        Assert.Equal(25, b.Bottom);
    }
}
