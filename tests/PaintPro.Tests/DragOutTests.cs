using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using SkiaSharp;
using Xunit;
using System.IO;
using IOPath = System.IO.Path;

namespace PaintPro.Tests;

/// <summary>
/// Плавающий объект можно утащить за край холста и там оставить (1.33.0). В Electron-версии
/// до 1.19.0 объект замирал там, где указатель уходил с холста; здесь его ведёт захваченная
/// мышь (Surface.CaptureMouse), а сдвиг ничем не обрезан. Эти проверки держат оба условия:
/// ни сдвиг, ни прижатие не притягивают объект обратно и ни о чём не предупреждают.
/// </summary>
public class DragOutTests
{
    private static (Document doc, PixelLayer layer, ToolContext ctx, List<string> hints) Make()
    {
        var doc = new Document(200, 150);
        var layer = (PixelLayer)doc.ActiveLayer;
        using (var c = new SKCanvas(layer.Bitmap))
        using (var p = new SKPaint { Color = SKColors.Red })
            c.DrawRect(new SKRect(50, 40, 100, 80), p);
        var hints = new List<string>();
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Black, Opacity = 1f, ReportHint = hints.Add };
        doc.Selection = new RectSelection(50, 40, 50, 40);
        return (doc, layer, ctx, hints);
    }

    private static int RedPixels(PixelLayer layer)
    {
        int n = 0;
        for (int y = 0; y < layer.Bitmap.Height; y++)
            for (int x = 0; x < layer.Bitmap.Width; x++)
                if (layer.Bitmap.GetPixel(x, y) == SKColors.Red) n++;
        return n;
    }

    public static IEnumerable<object[]> FarPoints() => new[]
    {
        new object[] { 5000f, 60f },
        new object[] { -5000f, 60f },
        new object[] { 75f, 9000f },
        new object[] { 75f, -9000f },
        new object[] { -3000f, 4000f },
        new object[] { 100000f, 100000f },
    };

    [Theory]
    [MemberData(nameof(FarPoints))]
    // объект идёт за указателем куда угодно - ровно на сдвиг указателя
    public void the_object_follows_the_pointer_anywhere(float tx, float ty)
    {
        var (doc, _, ctx, hints) = Make();
        var tool = new SelectTool();
        tool.OnPointerDown(new SKPoint(75, 60), ctx);
        var fp = doc.FloatingPickup!;
        float x0 = fp.X, y0 = fp.Y;
        tool.OnPointerMove(new SKPoint(tx, ty), ctx);
        Assert.Equal(x0 + tx - 75, fp.X, 3);
        Assert.Equal(y0 + ty - 60, fp.Y, 3);
        tool.OnPointerUp(new SKPoint(tx, ty), ctx);
        Assert.Same(fp, doc.FloatingPickup);
        Assert.Equal(x0 + tx - 75, fp.X, 3);   // отпустили - не притянулся обратно
        Assert.Empty(hints);
    }

    [Fact]
    // на всём пути объект нигде не замирает: каждый шаг - за указателем
    public void no_step_of_the_way_stalls()
    {
        var (doc, _, ctx, _) = Make();
        var tool = new SelectTool();
        tool.OnPointerDown(new SKPoint(75, 60), ctx);
        var fp = doc.FloatingPickup!;
        float x0 = fp.X, y0 = fp.Y;
        for (int i = 1; i <= 200; i++)
        {
            var p = new SKPoint(75 + i * 37, 60 - i * 23);
            tool.OnPointerMove(p, ctx);
            Assert.Equal(x0 + i * 37, fp.X, 2);
            Assert.Equal(y0 - i * 23, fp.Y, 2);
        }
    }

    [Fact]
    // вытащили и вернули - ложится туда, куда привели
    public void out_and_back_lands_where_led()
    {
        var (doc, layer, ctx, _) = Make();
        var tool = new SelectTool();
        tool.OnPointerDown(new SKPoint(75, 60), ctx);
        tool.OnPointerMove(new SKPoint(4000, -2000), ctx);
        tool.OnPointerMove(new SKPoint(85, 70), ctx);
        tool.OnPointerUp(new SKPoint(85, 70), ctx);
        doc.CommitFloating();
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(105, 85));
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(55, 45));
        Assert.Equal(50 * 40, RedPixels(layer));
    }

    [Fact]
    // брошенный за холстом объект молча уходит из картинки: одна запись истории, без подсказок
    public void a_dropped_off_canvas_object_silently_leaves_the_picture()
    {
        var (doc, layer, ctx, hints) = Make();
        var tool = new SelectTool();
        int before = doc.History.Cursor;
        tool.OnPointerDown(new SKPoint(75, 60), ctx);
        tool.OnPointerMove(new SKPoint(9000, 9000), ctx);
        tool.OnPointerUp(new SKPoint(9000, 9000), ctx);
        doc.CommitFloating();
        Assert.Null(doc.FloatingPickup);
        Assert.Equal(0, RedPixels(layer));
        Assert.Equal(before + 1, doc.History.Cursor);
        Assert.Empty(hints);
        doc.History.Undo(doc);
        Assert.Equal(50 * 40, RedPixels(layer));
    }

    [Fact]
    // наполовину за краем - на холсте остаётся ровно видимая часть
    public void half_outside_keeps_exactly_the_visible_part()
    {
        var (doc, layer, ctx, _) = Make();
        var tool = new SelectTool();
        tool.OnPointerDown(new SKPoint(75, 60), ctx);
        // левый край 50 -> 175: на холсте 25 столбцов из 50
        tool.OnPointerMove(new SKPoint(200, 60), ctx);
        tool.OnPointerUp(new SKPoint(200, 60), ctx);
        doc.CommitFloating();
        Assert.Equal(25 * 40, RedPixels(layer));
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(199, 60));
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(174, 60));
    }

    [Fact]
    // четырёхугольник уезжает за край целиком: сдвиг один на все четыре угла
    public void a_quad_moves_out_with_all_four_corners()
    {
        var (doc, _, ctx, _) = Make();
        var tool = new SelectTool();
        tool.OnPointerDown(new SKPoint(75, 60), ctx);
        var fp = doc.FloatingPickup!;
        fp.Quad = new[] { new SKPoint(50, 40), new SKPoint(100, 42), new SKPoint(98, 80), new SKPoint(52, 78) };
        var before = fp.Quad.ToArray();
        tool.OnPointerMove(new SKPoint(75 + 3000, 60 + 1700), ctx);
        for (int i = 0; i < 4; i++)
            {
                Assert.Equal(before[i].X + 3000, fp.Quad![i].X, 2);
                Assert.Equal(before[i].Y + 1700, fp.Quad![i].Y, 2);
            }
        Assert.Equal(50 + 3000, fp.X, 2);
    }

    [Fact]
    // в коде сдвига нет обрезки по холсту: ни Clamp, ни Min/Max с размерами документа
    public void the_move_code_has_no_clamp()
    {
        var root = RepoRoot();
        var select = File.ReadAllText(IOPath.Combine(root, "src", "PaintPro.Wpf", "Tools", "SelectTool.cs"));
        var move = select[select.IndexOf("public void OnPointerMove")..select.IndexOf("public void OnPointerUp")];
        Assert.DoesNotContain("Clamp", move);
        Assert.DoesNotContain("CanvasWidth", move);
        var ops = File.ReadAllText(IOPath.Combine(root, "src", "PaintPro.Wpf", "Services", "PickupOps.cs"));
        int t = ops.IndexOf("public static void Translate");
        var translate = ops[t..ops.IndexOf("\n    }", t)];
        Assert.DoesNotContain("Clamp", translate);
        Assert.DoesNotContain("CanvasWidth", translate);
    }

    [Fact]
    // и захват мыши у поверхности на время жеста есть: без него указатель за окном терялся бы
    public void the_surface_captures_the_mouse_for_a_gesture()
    {
        var view = File.ReadAllText(IOPath.Combine(RepoRoot(), "src", "PaintPro.Wpf", "Views", "CanvasView.xaml.cs"));
        Assert.Contains("Surface.CaptureMouse()", view);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(IOPath.Combine(dir.FullName, "PaintPro.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root");
    }
}
