using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using PaintPro.Services;
using PaintPro.ViewModels;
using PaintPro.Views;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Где стоит холст, который меньше окна (1.29.0).
///
/// В Electron-версии он посередине по ширине и у верхнего края (justify-content: safe
/// center, align-items: safe flex-start). В C#-версии подложка была прижата влево, и при
/// уменьшении масштаба холст съёживался к левому верхнему углу, а не к середине верхнего
/// края. Проверки ниже ставят вид в окно 1200x900 и смотрят, где на самом деле оказался
/// растр холста.
/// </summary>
public class CanvasCenteringTests
{
    private const double ViewW = 1200, ViewH = 900, Tol = 1.0;

    private static (CanvasView View, FrameworkElement Skia, ScrollViewer Scroll) Show(MainViewModel vm)
    {
        var view = new CanvasView { DataContext = vm };
        view.Measure(new Size(ViewW, ViewH));
        view.Arrange(new Rect(0, 0, ViewW, ViewH));
        view.UpdateLayout();
        return (view, (FrameworkElement)view.FindName("Skia")!, (ScrollViewer)view.FindName("Scroll")!);
    }

    private static MainViewModel Sized(int w, int h, double zoom)
    {
        var vm = new MainViewModel();
        if (w != vm.Document.CanvasWidth || h != vm.Document.CanvasHeight) Assert.True(vm.ResizeCanvasTo(w, h, 0, 0));
        vm.Zoom = zoom;
        return vm;
    }

    /// <summary>Левый верхний угол растра холста в координатах окна просмотра.</summary>
    private static Point Origin(FrameworkElement skia, ScrollViewer scroll)
        => skia.TranslatePoint(new Point(0, 0), scroll);

    public static IEnumerable<object[]> SmallCases()
    {
        // Холст по умолчанию на всех ступенях, где он уже окна просмотра.
        foreach (var z in new[] { 0.1, 0.25, 0.5, 0.67, 0.75, 1.0 })
            yield return new object[] { 900, 600, z };
        // Узкие и высокие: по ширине посередине, по высоте прокрутка - и всё равно посередине.
        foreach (var z in new[] { 0.25, 0.5, 1.0, 2.0 })
            yield return new object[] { 200, 1500, z };
        yield return new object[] { 1, 1, 1.0 };
        yield return new object[] { 50, 50, 8.0 };
        yield return new object[] { 1920, 1080, 0.25 };
        yield return new object[] { 1920, 1080, 0.5 };
        yield return new object[] { 4000, 3000, 0.1 };
        yield return new object[] { 1000, 20, 1.0 };
    }

    [Theory]
    [MemberData(nameof(SmallCases))]
    // холст уже окна - стоит посередине по ширине
    public void a_canvas_narrower_than_the_view_stands_in_the_middle(int w, int h, double zoom)
    {
        WpfRunner.Run(() =>
        {
            var (_, skia, scroll) = Show(Sized(w, h, zoom));
            var (sw, _) = ViewGeometry.SurfaceSize(w, h, zoom);
            var (cw, _) = ViewGeometry.ContentSize(sw, 0);
            Assert.True(cw < scroll.ViewportWidth, $"подложка {cw} не уже окна {scroll.ViewportWidth}");

            var o = Origin(skia, scroll);
            double expected = (scroll.ViewportWidth - cw) / 2 + ViewGeometry.CanvasMargin;
            Assert.InRange(o.X, expected - Tol, expected + Tol);
            // Середина растра - на середине окна: уменьшаем масштаб, и холст сжимается к ней.
            Assert.InRange(o.X + skia.ActualWidth / 2, scroll.ViewportWidth / 2 - Tol, scroll.ViewportWidth / 2 + Tol);
        });
    }

    [Theory]
    [MemberData(nameof(SmallCases))]
    // и при этом у верхнего края, как в Electron-версии
    public void a_small_canvas_stays_at_the_top(int w, int h, double zoom)
    {
        WpfRunner.Run(() =>
        {
            var (_, skia, scroll) = Show(Sized(w, h, zoom));
            Assert.InRange(Origin(skia, scroll).Y, ViewGeometry.CanvasMargin - Tol, ViewGeometry.CanvasMargin + Tol);
        });
    }

    [Theory]
    [InlineData(900, 600, 2.0)]
    [InlineData(900, 600, 4.0)]
    [InlineData(900, 600, 8.0)]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(4000, 3000, 0.5)]
    [InlineData(3000, 100, 1.0)]
    // холст шире окна - с левого поля, прокрутка начинается с края, как раньше
    public void a_canvas_wider_than_the_view_starts_at_the_margin(int w, int h, double zoom)
    {
        WpfRunner.Run(() =>
        {
            var (_, skia, scroll) = Show(Sized(w, h, zoom));
            Assert.True(scroll.ExtentWidth > scroll.ViewportWidth, "холст должен быть шире окна");
            Assert.Equal(0, scroll.HorizontalOffset);
            var o = Origin(skia, scroll);
            Assert.InRange(o.X, ViewGeometry.CanvasMargin - Tol, ViewGeometry.CanvasMargin + Tol);
            Assert.InRange(o.Y, ViewGeometry.CanvasMargin - Tol, ViewGeometry.CanvasMargin + Tol);
        });
    }

    [Theory]
    [InlineData(900, 600, 4.0, 700)]
    [InlineData(1920, 1080, 1.0, 400)]
    // широкий холст прокручивается до правого края и не уезжает за него
    public void a_wide_canvas_scrolls_to_its_right_edge(int w, int h, double zoom, double offset)
    {
        WpfRunner.Run(() =>
        {
            var (view, skia, scroll) = Show(Sized(w, h, zoom));
            scroll.ScrollToHorizontalOffset(offset);
            view.UpdateLayout();
            Assert.Equal(offset, scroll.HorizontalOffset, 3);
            Assert.InRange(Origin(skia, scroll).X, ViewGeometry.CanvasMargin - offset - Tol, ViewGeometry.CanvasMargin - offset + Tol);

            scroll.ScrollToRightEnd();
            view.UpdateLayout();
            // Правое поле видно целиком: справа от растра ровно CanvasMargin до края окна.
            double right = Origin(skia, scroll).X + skia.ActualWidth;
            Assert.InRange(scroll.ViewportWidth - right, ViewGeometry.CanvasMargin - Tol, ViewGeometry.CanvasMargin + Tol);
        });
    }

    [Fact]
    // масштаб по шагам вниз: середина верхнего края стоит на месте
    public void zooming_out_step_by_step_keeps_the_top_middle_point_still()
    {
        WpfRunner.Run(() =>
        {
            var vm = Sized(900, 600, 1.0);
            var (view, skia, scroll) = Show(vm);
            var marks = new List<(double MidX, double Top)>();
            foreach (var z in new[] { 1.0, 0.75, 0.67, 0.5, 0.25, 0.1 })
            {
                vm.Zoom = z;
                view.UpdateLayout();
                var o = Origin(skia, scroll);
                marks.Add((o.X + skia.ActualWidth / 2, o.Y));
            }
            foreach (var (midX, top) in marks)
            {
                Assert.InRange(midX, marks[0].MidX - Tol, marks[0].MidX + Tol);
                Assert.InRange(top, marks[0].Top - Tol, marks[0].Top + Tol);
            }
        });
    }

    [Fact]
    // окно стало шире - холст переезжает на новую середину
    public void the_canvas_follows_the_middle_when_the_view_is_resized()
    {
        WpfRunner.Run(() =>
        {
            var (view, skia, scroll) = Show(Sized(900, 600, 0.5));
            foreach (var width in new[] { 800.0, 1000, 1400, 1900 })
            {
                view.Measure(new Size(width, ViewH));
                view.Arrange(new Rect(0, 0, width, ViewH));
                view.UpdateLayout();
                var o = Origin(skia, scroll);
                Assert.InRange(o.X + skia.ActualWidth / 2, scroll.ViewportWidth / 2 - Tol, scroll.ViewportWidth / 2 + Tol);
            }
        });
    }

    [Fact]
    // ручки холста и рамка выделения едут вместе с ним: накладка в том же месте, что растр
    public void the_overlay_moves_together_with_the_centred_canvas()
    {
        WpfRunner.Run(() =>
        {
            var (view, skia, scroll) = Show(Sized(900, 600, 0.5));
            var overlay = (FrameworkElement)view.FindName("Overlay")!;
            var frame = (FrameworkElement)view.FindName("CanvasFrame")!;
            var s = Origin(skia, scroll);
            foreach (var el in new[] { overlay, frame })
            {
                var o = el.TranslatePoint(new Point(0, 0), scroll);
                Assert.InRange(o.X, s.X - Tol, s.X + Tol);
                Assert.InRange(o.Y, s.Y - Tol, s.Y + Tol);
            }
        });
    }

    // ───────── разметка и Electron-версия ─────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaintPro.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    // в разметке подложка - Center по ширине и Top по высоте
    public void the_markup_centres_the_canvas_horizontally_and_pins_it_to_the_top()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "PaintPro.Wpf", "Views", "CanvasView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var root = doc.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ContentRoot");
        Assert.Equal("Center", (string?)root.Attribute("HorizontalAlignment"));
        Assert.Equal("Top", (string?)root.Attribute("VerticalAlignment"));
    }

    [Fact]
    // в Electron-версии то же самое: safe center по ширине, safe flex-start по высоте
    public void the_electron_version_places_the_canvas_the_same_way()
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "paint-pro-electron", "paint-pro.html"));
        // Правил .canvas-area в файле несколько (узкое окно задаёт только min-width) -
        // берём то, где раскладка.
        var rule = Regex.Matches(html, @"\.canvas-area \{([^}]*)\}")
            .Select(m => m.Groups[1].Value)
            .SingleOrDefault(body => body.Contains("justify-content"));
        Assert.NotNull(rule);
        Assert.Matches(@"justify-content:\s*safe center", rule!);
        Assert.Matches(@"align-items:\s*safe flex-start", rule!);
    }
}
