using System.IO;
using IOPath = System.IO.Path;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
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
        foreach (var size in new[] { 1, 4, 60, 150, 300 })
            yield return new object[] { tool, size };
    }

    [Theory]
    [MemberData(nameof(ToolsAndSizes))]
    // диаметр кружка - размер инструмента
    public void the_ring_diameter_is_the_tool_size(ToolKind tool, int size)
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(tool, size);
            view.MoveBrushRing(new Point(300, 200));
            Assert.Equal(Visibility.Visible, Ring(view).Visibility);
            Assert.Equal(size, Ring(view).Width, 6);
            Assert.Equal(size, Ring(view).Height, 6);
            Assert.Equal(size, Outer(view).Width, 6);
        });
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(2.0)]
    [InlineData(8.0)]
    // на масштабе - в экранных точках: размер, умноженный на масштаб
    public void the_ring_follows_the_zoom(double zoom)
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(ToolKind.Brush, 100, zoom);
            view.MoveBrushRing(new Point(50, 50));
            Assert.Equal(100 * zoom, Ring(view).Width, 6);
        });
    }

    [Fact]
    // центр кружка - точка под курсором
    public void the_ring_is_centred_on_the_pointer()
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(ToolKind.Brush, 80);
            view.MoveBrushRing(new Point(333, 222));
            var (cx, cy, _) = Geometry(Ring(view));
            Assert.Equal(333, cx, 6);
            Assert.Equal(222, cy, 6);
            var (ox, oy, _) = Geometry(Outer(view));
            Assert.Equal(333, ox, 6);
            Assert.Equal(222, oy, 6);
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
    // колесо (размер) меняет кружок сразу, без движения мыши
    public void the_ring_follows_the_size_at_once()
    {
        WpfRunner.Run(() =>
        {
            var (view, vm) = Show(ToolKind.Brush, 40);
            view.MoveBrushRing(new Point(100, 100));
            vm.AdjustToolSize(up: true);
            Assert.Equal(vm.ToolSize, Ring(view).Width, 6);
            Assert.True(vm.ToolSize > 40);
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
            Assert.Equal(80, Ring(view).Width, 6);
        });
    }

    [Fact]
    // курсор ушёл с холста - кружка нет
    public void the_ring_hides_when_the_pointer_leaves()
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(ToolKind.Brush, 40);
            view.MoveBrushRing(new Point(100, 100));
            view.MoveBrushRing(null);
            Assert.Equal(Visibility.Collapsed, Ring(view).Visibility);
            Assert.Equal(Visibility.Collapsed, Outer(view).Visibility);
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
    // у инструментов без толщины кружка нет
    public void tools_without_a_width_have_no_ring(ToolKind tool)
    {
        WpfRunner.Run(() =>
        {
            var (view, _) = Show(tool, 40);
            view.MoveBrushRing(new Point(100, 100));
            Assert.Equal(Visibility.Collapsed, Ring(view).Visibility);
        });
    }

    [Fact]
    // сменили инструмент на выделение - кружок пропал, вернули кисть - вернулся
    public void switching_tools_shows_and_hides_the_ring()
    {
        WpfRunner.Run(() =>
        {
            var (view, vm) = Show(ToolKind.Brush, 40);
            view.MoveBrushRing(new Point(100, 100));
            vm.ActiveTool = ToolKind.Select;
            Assert.Equal(Visibility.Collapsed, Ring(view).Visibility);
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
