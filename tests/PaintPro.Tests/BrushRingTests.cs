using System.IO;
using IOPath = System.IO.Path;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.ViewModels;
using PaintPro.Views;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Кружок размера кисти и настоящая толщина инструментов (1.32.0).
///
/// В C#-версии кружка не было вовсе - только крестик, и какой толщины ляжет линия, было
/// видно лишь после нажатия. В Electron-версии кружок был, но курсором-картинкой, которую
/// Chromium не показывает больше 128 точек: на кисти 300 px он был втрое меньше. И размер
/// значил у инструментов разное: карандаш рисовал вдвое тоньше, в Electron маркер - в
/// полтора раза толще, «300 px» у ластика и у карандаша были разными полосами. Теперь
/// размер - это толщина у всех, а кружок в обеих версиях ровно такого диаметра.
/// </summary>
public class BrushRingTests
{
    private static (CanvasView View, MainViewModel Vm) Show(ToolKind tool, int size, double zoom = 1.0)
    {
        var vm = new MainViewModel { ActiveTool = tool, ToolSize = size, Zoom = zoom };
        var view = new CanvasView { DataContext = vm };
        view.Measure(new Size(1200, 900));
        view.Arrange(new Rect(0, 0, 1200, 900));
        view.UpdateLayout();
        return (view, vm);
    }

    private static Ellipse Ring(CanvasView v) => (Ellipse)v.FindName("BrushRing")!;
    private static Ellipse Outer(CanvasView v) => (Ellipse)v.FindName("BrushRingOuter")!;

    private static (double CX, double CY, double D) Geometry(Ellipse e)
        => (Canvas.GetLeft(e) + e.Width / 2, Canvas.GetTop(e) + e.Height / 2, e.Width);

    public static IEnumerable<object[]> ToolsAndSizes()
    {
        foreach (var tool in new[] { ToolKind.Pencil, ToolKind.Brush, ToolKind.Marker, ToolKind.Eraser, ToolKind.Line, ToolKind.Rect })
        foreach (var size in new[] { 1, 4, 60, 120, 121, 150, 300 })
            yield return new object[] { tool, size };
    }

    [Theory]
    [MemberData(nameof(ToolsAndSizes))]
    // диаметр кружка - размер инструмента: до 120 в курсоре, больше - элементом
    public void the_ring_diameter_is_the_tool_size(ToolKind tool, int size)
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(tool, size);
            view.MoveBrushRing(new Point(300, 200));
            if (size <= RingCursor.MaxDiameter)
            {
                Assert.Equal(size, view.BrushCursorDiameter!.Value, 6);
                Assert.Equal(Visibility.Collapsed, Ring(view).Visibility);
                Assert.Same(view.BrushCursor, view.Cursor);
            }
            else
            {
                Assert.Null(view.BrushCursorDiameter);
                Assert.Same(Cursors.None, view.Cursor);
                Assert.Equal(Visibility.Visible, Ring(view).Visibility);
                Assert.Equal(size, Ring(view).Width, 6);
                Assert.Equal(size, Ring(view).Height, 6);
                Assert.Equal(size, Outer(view).Width, 6);
            }
        });
    }

    [Theory]
    [InlineData(0.25, false)]
    [InlineData(0.5, false)]
    [InlineData(2.0, true)]
    [InlineData(8.0, true)]
    // на масштабе - в экранных точках: размер, умноженный на масштаб
    public void the_ring_follows_the_zoom(double zoom, bool element)
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(ToolKind.Brush, 100, zoom);
            view.MoveBrushRing(new Point(50, 50));
            double d = element ? Ring(view).Width : view.BrushCursorDiameter!.Value;
            Assert.Equal(100 * zoom, d, 6);
        });
    }

    [Fact]
    // большой кружок и его крестик - центром под указателем
    public void the_big_ring_and_its_cross_are_centred_on_the_pointer()
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(ToolKind.Brush, 200);
            view.MoveBrushRing(new Point(333, 222));
            var (cx, cy, _) = Geometry(Ring(view));
            Assert.Equal(333, cx, 6);
            Assert.Equal(222, cy, 6);
            var (ox, oy, _) = Geometry(Outer(view));
            Assert.Equal(333, ox, 6);
            Assert.Equal(222, oy, 6);
            var cross = (FrameworkElement)view.FindName("BrushCross")!;
            Assert.Equal(Visibility.Visible, cross.Visibility);
            Assert.Equal(333, Canvas.GetLeft(cross), 6);
            Assert.Equal(222, Canvas.GetTop(cross), 6);
        });
    }

    [Fact]
    // маленький кружок - только курсор: ни кружка-элемента, ни нарисованного крестика
    public void a_small_ring_is_only_the_cursor()
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(ToolKind.Brush, 40);
            view.MoveBrushRing(new Point(333, 222));
            Assert.Equal(Visibility.Collapsed, Ring(view).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("BrushCross")!).Visibility);
            Assert.NotNull(view.BrushCursor);
        });
    }

    [Fact]
    // слой кружка лежит ровно на поверхности: точка поверхности - та же точка экрана
    public void the_ring_layer_sits_exactly_on_the_surface()
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(ToolKind.Brush, 20, 0.5);
            var surface = (FrameworkElement)view.FindName("Surface")!;
            var layer = (FrameworkElement)view.FindName("RingLayer")!;
            var a = layer.TranslatePoint(new Point(0, 0), surface);
            Assert.Equal(0, a.X, 6);
            Assert.Equal(0, a.Y, 6);
            Assert.Equal(surface.ActualWidth, layer.ActualWidth, 6);
        });
    }

    [Fact]
    // колесо (размер) меняет кружок сразу, без движения мыши, и через границу 120 тоже
    public void the_ring_follows_the_size_at_once()
    {
        WpfRunner.Run(() =>
        {
            var (view, vm) = Show(ToolKind.Brush, 40);
            view.MoveBrushRing(new Point(100, 100));
            vm.AdjustToolSize(up: true);
            Assert.Equal(vm.ToolSize, view.BrushCursorDiameter!.Value, 6);
            vm.ToolSize = 200;
            Assert.Null(view.BrushCursorDiameter);
            Assert.Equal(200, Ring(view).Width, 6);
            vm.ToolSize = 50;
            Assert.Equal(50, view.BrushCursorDiameter!.Value, 6);
            Assert.Equal(Visibility.Collapsed, Ring(view).Visibility);
        });
    }

    [Fact]
    // масштаб поменяли - кружок тоже
    public void the_ring_follows_the_zoom_change_at_once()
    {
        WpfRunner.Run(() =>
        {
            var (view, vm) = Show(ToolKind.Brush, 40);
            view.MoveBrushRing(new Point(100, 100));
            vm.Zoom = 2;
            Assert.Equal(80, view.BrushCursorDiameter!.Value, 6);
            vm.Zoom = 4;
            Assert.Equal(160, Ring(view).Width, 6);
        });
    }

    [Fact]
    // курсор ушёл с холста - большого кружка нет
    public void the_ring_hides_when_the_pointer_leaves()
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(ToolKind.Brush, 200);
            view.MoveBrushRing(new Point(100, 100));
            view.MoveBrushRing(null);
            Assert.Equal(Visibility.Collapsed, Ring(view).Visibility);
            Assert.Equal(Visibility.Collapsed, Outer(view).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("BrushCross")!).Visibility);
        });
    }

    [Theory]
    [InlineData(ToolKind.Select)]
    [InlineData(ToolKind.Fill)]
    [InlineData(ToolKind.Picker)]
    [InlineData(ToolKind.Text)]
    [InlineData(ToolKind.Hand)]
    [InlineData(ToolKind.Crop)]
    [InlineData(ToolKind.Quad)]
    // у инструментов без толщины кружка нет - ни в курсоре, ни элементом
    public void tools_without_a_width_have_no_ring(ToolKind tool)
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(tool, 200);
            view.MoveBrushRing(new Point(100, 100));
            Assert.Equal(Visibility.Collapsed, Ring(view).Visibility);
            Assert.Null(view.BrushCursor);
        });
    }

    [Fact]
    // сменили инструмент на выделение - кружок пропал, вернули кисть - вернулся
    public void switching_tools_shows_and_hides_the_ring()
    {
        WpfRunner.Run(() =>
        {
            var (view, vm) = Show(ToolKind.Brush, 200);
            view.MoveBrushRing(new Point(100, 100));
            vm.ActiveTool = ToolKind.Select;
            Assert.Equal(Visibility.Collapsed, Ring(view).Visibility);
            Assert.Null(view.BrushCursor);
            vm.ActiveTool = ToolKind.Eraser;
            Assert.Equal(Visibility.Visible, Ring(view).Visibility);
        });
    }

    [Fact]
    // кружок не ловит мышь: рисовать сквозь него
    public void the_ring_does_not_catch_the_mouse()
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(ToolKind.Brush, 300);
            Assert.False(((FrameworkElement)view.FindName("RingLayer")!).IsHitTestVisible);
        });
    }

    // ───────── курсор-картинка ─────────

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(37.5)]
    [InlineData(120)]
    // картинка курсора не больше 128, нечётная (центр - ровно пиксель), с полем под обводку
    public void the_cursor_image_fits_and_is_centred(double d)
    {
        int side = RingCursor.SideFor(d);
        Assert.True(side <= 128, $"{side}");
        Assert.Equal(1, side % 2);
        Assert.True(side >= d + RingCursor.Pad * 2);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    [InlineData(120)]
    // файл .cur: курсор, одна картинка, горячая точка в центре, 32 бита, маска
    public void the_cursor_file_is_a_well_formed_cur(double d)
    {
        var b = RingCursor.BuildCur(d);
        int side = RingCursor.SideFor(d);
        Assert.Equal(2, BitConverter.ToUInt16(b, 2));       // тип: курсор
        Assert.Equal(1, BitConverter.ToUInt16(b, 4));       // одна картинка
        Assert.Equal(side, b[6]);
        Assert.Equal(side, b[7]);
        Assert.Equal(side / 2, BitConverter.ToUInt16(b, 10)); // горячая точка
        Assert.Equal(side / 2, BitConverter.ToUInt16(b, 12));
        Assert.Equal(22, BitConverter.ToInt32(b, 18));
        Assert.Equal(40, BitConverter.ToInt32(b, 22));
        Assert.Equal(side * 2, BitConverter.ToInt32(b, 30));
        Assert.Equal(32, BitConverter.ToUInt16(b, 36));
        int maskStride = (side + 31) / 32 * 4;
        Assert.Equal(22 + 40 + side * side * 4 + maskStride * side, b.Length);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(80)]
    [InlineData(120)]
    // на картинке кружок нужного радиуса и крестик в центре, углы прозрачные
    public void the_cursor_image_draws_the_ring_and_the_cross(double d)
    {
        using var bmp = RingCursor.Render(d);
        int c = bmp.Width / 2;
        int r = (int)Math.Round(d / 2);
        Assert.True(bmp.GetPixel(c + r, c).Alpha > 100, "кружок справа");
        Assert.True(bmp.GetPixel(c, c - r).Alpha > 100, "кружок сверху");
        Assert.Equal(SKColors.Black, bmp.GetPixel(c, c).WithAlpha(255));
        Assert.True(bmp.GetPixel(c, c).Alpha > 200, "крестик в центре");
        Assert.Equal(0, bmp.GetPixel(0, 0).Alpha);
        // Внутри кружка, по диагонали (крестик идёт по осям), - пусто.
        int k = (int)Math.Round(r * 0.5);
        Assert.Equal(0, bmp.GetPixel(c + k, c + k).Alpha);
    }

    [Fact]
    // Windows принимает этот файл как курсор
    public void windows_loads_the_cursor()
    {
        WpfRunner.Run(() =>
        {
            using var cursor = RingCursor.Create(64);
            Assert.NotNull(cursor);
        });
    }

    [Fact]
    // граница курсора та же, что в Electron-версии
    public void the_cursor_limit_is_the_same_as_in_electron()
    {
        var html = File.ReadAllText(IOPath.Combine(RepoRoot(), "paint-pro-electron", "paint-pro.html"));
        var m = Regex.Match(html, @"const CURSOR_RING_MAX = (\d+);");
        Assert.True(m.Success);
        Assert.Equal(RingCursor.MaxDiameter, double.Parse(m.Groups[1].Value));
    }

    // ───────── настоящая толщина ─────────

    private static int BandHalfWidth(ToolKind tool, int size)
    {
        var vm = new MainViewModel { ActiveTool = tool, ToolSize = size, PrimaryColor = SKColors.Black };
        var paper = ((PixelLayer)vm.Document.Layers[0]).Bitmap;
        if (tool == ToolKind.Eraser) paper.Erase(SKColors.Black);
        var t = vm.ActiveToolInstance;
        t.OnPointerDown(new SKPoint(100, 300), vm.ToolContext);
        t.OnPointerMove(new SKPoint(800, 300), vm.ToolContext);
        t.OnPointerUp(new SKPoint(800, 300), vm.ToolContext);
        var untouched = tool == ToolKind.Eraser ? SKColors.Black : SKColors.White;
        int n = 0;
        for (int y = 300; y >= 0 && paper.GetPixel(450, y) != untouched; y--) n++;
        return n;
    }

    [Theory]
    [InlineData(ToolKind.Pencil, 60)]
    [InlineData(ToolKind.Brush, 60)]
    [InlineData(ToolKind.Marker, 60)]
    [InlineData(ToolKind.Eraser, 60)]
    [InlineData(ToolKind.Pencil, 300)]
    [InlineData(ToolKind.Eraser, 300)]
    // полоса - шириной в заданный размер, у всех инструментов одинаково
    public void every_tool_draws_a_band_as_wide_as_its_size(ToolKind tool, int size)
    {
        WpfRunner.Run(() =>
        {
            int half = BandHalfWidth(tool, size);
            Assert.InRange(half, size / 2 - 1, size / 2 + 2);
        });
    }

    [Fact]
    // «300 px» у карандаша и у ластика - одна и та же полоса
    public void the_pencil_and_the_eraser_agree_at_the_same_size()
    {
        WpfRunner.Run(() =>
            Assert.InRange(BandHalfWidth(ToolKind.Pencil, 300) - BandHalfWidth(ToolKind.Eraser, 300), -1, 1));
    }

    // ───────── то же, что в Electron-версии ─────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(IOPath.Combine(dir.FullName, "PaintPro.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    // в Electron-версии толщина тоже равна размеру у всех инструментов
    public void the_electron_version_draws_the_size_as_the_width_too()
    {
        var html = File.ReadAllText(IOPath.Combine(RepoRoot(), "paint-pro-electron", "paint-pro.html"));
        var m = Regex.Match(html, @"function strokeWidthFor\(tool, size\) \{([^}]*)\}");
        Assert.True(m.Success, "strokeWidthFor не найдена");
        Assert.Equal("return Math.max(1, size);", m.Groups[1].Value.Trim());
    }

    [Fact]
    // и кружок там - элемент, а не курсор-картинка с потолком
    public void the_electron_ring_is_an_element_not_a_capped_cursor()
    {
        var html = File.ReadAllText(IOPath.Combine(RepoRoot(), "paint-pro-electron", "paint-pro.html"));
        Assert.Contains("id=\"brush-ring\"", html);
        Assert.DoesNotContain("Math.min(96,", html);
    }
}
