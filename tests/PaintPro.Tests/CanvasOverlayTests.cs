using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using PaintPro.Views;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Накладка холста: рамка выделения, восемь ручек масштаба, ручка поворота, точки углов
/// маски и размер самой поверхности.
///
/// Всё это - настоящие элементы WPF, и до 1.21.0 их не проверял никто. Оказалось, что
/// поднять их в тестах можно: холст строится и раскладывается без окна, нужен только один
/// STA-поток на всех (<see cref="WpfRunner"/>).
/// </summary>
public class CanvasOverlayTests
{
    /// <summary>Поднять вид с уже подготовленным документом и получить его накладку.</summary>
    private static (CanvasView View, Canvas Overlay) Show(MainViewModel vm)
    {
        var view = new CanvasView { DataContext = vm };
        view.Measure(new Size(1200, 900));
        view.Arrange(new Rect(0, 0, 1200, 900));
        Redraw(view);
        var overlay = (Canvas)view.FindName("Overlay")!;
        return (view, overlay);
    }

    /// <summary>Пересобрать накладку сейчас же: обычный путь идёт через кадр композиции.</summary>
    private static void Redraw(CanvasView view)
        => typeof(CanvasView)
            .GetMethod("DrawOverlay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(view, null);

    private static MainViewModel Painted()
    {
        var vm = new MainViewModel();
        using var c = new SKCanvas(((PixelLayer)vm.Document.Layers[0]).Bitmap);
        c.DrawRect(new SKRect(50, 50, 250, 250), new SKPaint { Color = SKColors.Red });
        return vm;
    }

    private static IEnumerable<T> Kids<T>(Canvas c) => c.Children.OfType<T>();

    [Fact]
    // чистый документ - накладка пуста
    public void idle_overlay_is_empty()
    {
        WpfRunner.Run(() =>
        {
            var (_, overlay) = Show(new MainViewModel());
            Assert.Empty(overlay.Children);
        });
    }

    [Fact]
    // прямоугольная рамка рисуется
    public void rect_selection_draws_a_frame()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.Document.Selection = new RectSelection(50, 60, 100, 80);
            var (_, overlay) = Show(vm);
            var frame = Kids<Rectangle>(overlay).Single();
            Assert.Equal(100, frame.Width, 3);
            Assert.Equal(80, frame.Height, 3);
            Assert.Equal(50, Canvas.GetLeft(frame), 3);
            Assert.Equal(60, Canvas.GetTop(frame), 3);
        });
    }

    [Fact]
    // рамка масштабируется вместе с зумом
    public void frame_follows_the_zoom()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.Zoom = 2.0;
            vm.Document.Selection = new RectSelection(50, 60, 100, 80);
            var (_, overlay) = Show(vm);
            var frame = Kids<Rectangle>(overlay).Single();
            Assert.Equal(200, frame.Width, 3);
            Assert.Equal(100, Canvas.GetLeft(frame), 3);
        });
    }

    [Fact]
    // рамка не перехватывает клики - иначе выделение нельзя было бы поднять
    public void frame_does_not_swallow_clicks()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.Document.Selection = new RectSelection(50, 60, 100, 80);
            var (_, overlay) = Show(vm);
            Assert.False(Kids<Rectangle>(overlay).Single().IsHitTestVisible);
        });
    }

    [Fact]
    // многоугольник рисует контур и четыре точки
    public void polygon_draws_four_dots()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.Document.Selection = new PolygonSelection(
                new SKPoint(50, 50), new SKPoint(150, 60),
                new SKPoint(140, 160), new SKPoint(45, 150));
            var (_, overlay) = Show(vm);
            Assert.Single(Kids<Polygon>(overlay));
            Assert.Equal(4, Kids<Ellipse>(overlay).Count());
        });
    }

    [Fact]
    // поднятый объект даёт рамку, восемь ручек, ручку поворота и поводок
    public void pickup_draws_every_handle()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            PickupOps.PromoteRect(vm.Document, new SKRect(50, 50, 250, 250));
            var (_, overlay) = Show(vm);
            // рамка + 8 квадратных ручек = 9 прямоугольников
            Assert.Equal(9, Kids<Rectangle>(overlay).Count());
            Assert.Single(Kids<Ellipse>(overlay));   // ручка поворота
            Assert.Single(Kids<Line>(overlay));      // поводок к ней
        });
    }

    [Fact]
    // во время жеста ручки спрятаны, а рамка остаётся
    public void handles_hide_while_drawing()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            PickupOps.PromoteRect(vm.Document, new SKRect(50, 50, 250, 250));
            vm.ToolContext.IsDrawing = true;
            var (_, overlay) = Show(vm);
            Assert.Single(Kids<Rectangle>(overlay));  // одна только рамка
            Assert.Empty(Kids<Ellipse>(overlay));
        });
    }

    [Fact]
    // ручки стоят там же, куда их кладёт вынесенная арифметика
    public void handle_positions_match_the_geometry()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.Zoom = 2.0;
            PickupOps.PromoteRect(vm.Document, new SKRect(50, 50, 250, 250));
            var fp = vm.Document.FloatingPickup!;
            var (_, overlay) = Show(vm);

            var nw = Kids<Rectangle>(overlay)
                .Single(r => r.Tag is ResizeHandle h && h == ResizeHandle.NW);
            double size = PickupOps.HandleSize(fp, vm.Zoom, 12);
            var expected = ViewGeometry.HandleTopLeft(
                ViewGeometry.ResizeHandleAnchor(fp, ResizeHandle.NW), vm.Zoom, size);
            Assert.Equal(expected.Left, Canvas.GetLeft(nw), 3);
            Assert.Equal(expected.Top, Canvas.GetTop(nw), 3);
        });
    }

    [Fact]
    // у маленького объекта ручки ужимаются
    public void handles_shrink_for_a_small_object()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            PickupOps.PromoteRect(vm.Document, new SKRect(50, 50, 58, 58));
            var (_, overlay) = Show(vm);
            var handle = Kids<Rectangle>(overlay).First(r => r.Tag is ResizeHandle);
            Assert.True(handle.Width <= 4.001, $"ручка {handle.Width} на объекте 8x8");
        });
    }

    [Fact]
    // у повёрнутого объекта точки маски стоят повёрнутыми
    public void rotated_quad_dots_follow_the_rotation()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            PickupOps.PromoteQuad(vm.Document, new[]
            {
                new SKPoint(50, 50), new SKPoint(250, 50),
                new SKPoint(250, 250), new SKPoint(50, 250),
            });
            var fp = vm.Document.FloatingPickup!;
            fp.SetRotation(MathF.PI / 2f);
            var (_, overlay) = Show(vm);

            var dots = Kids<Ellipse>(overlay).Where(e => e.Tag is null).ToList();
            Assert.Equal(4, dots.Count);
            var expected = GeometryMath.Rotate(fp.Quad![0], fp.Center, fp.Rotation);
            Assert.Contains(dots, d =>
                Math.Abs(Canvas.GetLeft(d) + 4.5 - expected.X) < 0.5
                && Math.Abs(Canvas.GetTop(d) + 4.5 - expected.Y) < 0.5);
        });
    }

    [Fact]
    // поверхность холста равна размеру документа на масштабе 1:1
    public void surface_matches_the_document()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var (view, _) = Show(vm);
            var skia = (FrameworkElement)view.FindName("Skia")!;
            Assert.Equal(vm.Document.CanvasWidth, skia.Width, 3);
            Assert.Equal(vm.Document.CanvasHeight, skia.Height, 3);
        });
    }

    [Fact]
    // смена размера холста доходит до поверхности
    public void canvas_resize_reaches_the_surface()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var (view, _) = Show(vm);
            vm.Document.History.ExecuteAndPush(new Commands.ResizeCanvasCommand(300, 200), vm.Document);
            var skia = (FrameworkElement)view.FindName("Skia")!;
            Assert.Equal(300, skia.Width, 3);
            Assert.Equal(200, skia.Height, 3);
        });
    }

    [Fact]
    // подложка шире поверхности на два поля
    public void content_keeps_the_margin()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var (view, _) = Show(vm);
            var skia = (FrameworkElement)view.FindName("Skia")!;
            var content = (FrameworkElement)view.FindName("ContentRoot")!;
            Assert.Equal(skia.Width + ViewGeometry.CanvasMargin * 2, content.Width, 3);
        });
    }

    [Fact]
    // накладка стоит ровно поверх поверхности
    public void overlay_covers_the_surface()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.Zoom = 4.0;
            var (view, overlay) = Show(vm);
            var skia = (FrameworkElement)view.FindName("Skia")!;
            Assert.Equal(skia.Width, overlay.Width, 3);
            Assert.Equal(skia.Height, overlay.Height, 3);
            Assert.Equal(skia.Margin, overlay.Margin);
        });
    }

    [Fact]
    // точки углов не перехватывают клики - хват считает сам инструмент
    public void corner_dots_do_not_swallow_clicks()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.Document.Selection = new PolygonSelection(
                new SKPoint(50, 50), new SKPoint(150, 60),
                new SKPoint(140, 160), new SKPoint(45, 150));
            var (_, overlay) = Show(vm);
            Assert.All(Kids<Ellipse>(overlay), e => Assert.False(e.IsHitTestVisible));
        });
    }

    [Fact]
    // у ручек есть курсоры, иначе непонятно, что за них тянут
    public void handles_carry_cursors()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            PickupOps.PromoteRect(vm.Document, new SKRect(50, 50, 250, 250));
            var (_, overlay) = Show(vm);
            Assert.All(Kids<Rectangle>(overlay).Where(r => r.Tag is ResizeHandle),
                r => Assert.NotNull(r.Cursor));
        });
    }
}
