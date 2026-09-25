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

    /// <summary>
    /// Прямоугольники ВЫДЕЛЕНИЯ и поднятого объекта: без ручек самого холста.
    ///
    /// С 1.25.0 холст растягивается за края, и восемь его ручек лежат в той же накладке.
    /// Считать всё подряд значило бы, что проверка рамки выделения сломается от каждой
    /// правки в ручках холста - и наоборот.
    /// </summary>
    private static IEnumerable<Rectangle> Rects(Canvas c)
        => Kids<Rectangle>(c).Where(r => r.Tag is not CanvasEdgeTag);

    /// <summary>Ручки самого холста.</summary>
    private static IEnumerable<Rectangle> CanvasHandles(Canvas c)
        => Kids<Rectangle>(c).Where(r => r.Tag is CanvasEdgeTag);

    [Fact]
    // чистый документ - в накладке только ручки холста
    public void idle_overlay_holds_only_the_canvas_handles()
    {
        WpfRunner.Run(() =>
        {
            var (_, overlay) = Show(new MainViewModel());
            Assert.Empty(Rects(overlay));
            Assert.Equal(8, CanvasHandles(overlay).Count());
            Assert.Equal(overlay.Children.Count, CanvasHandles(overlay).Count());
        });
    }

    [Fact]
    // у холста восемь ручек, все разные и все ловят мышь
    public void canvas_has_eight_distinct_handles()
    {
        WpfRunner.Run(() =>
        {
            var (_, overlay) = Show(new MainViewModel());
            var handles = CanvasHandles(overlay).ToList();

            var edges = handles.Select(h => ((CanvasEdgeTag)h.Tag!).Edge).ToList();
            Assert.Equal(8, edges.Distinct().Count());
            Assert.Equal(Enum.GetValues<ResizeHandle>().OrderBy(x => x), edges.OrderBy(x => x));

            // Ручка, которая не ловит мышь или не показывает курсор, - нарисованная
            // картинка, а не ручка: тянуть её пользователь не догадается.
            Assert.All(handles, h =>
            {
                Assert.True(h.IsHitTestVisible);
                Assert.NotNull(h.Cursor);
            });
        });
    }

    [Fact]
    // ручки холста стоят за его краем, а не поверх рисунка
    public void canvas_handles_stay_outside_the_surface()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var (view, overlay) = Show(vm);
            var skia = (FrameworkElement)view.FindName("Surface")!;

            Assert.All(CanvasHandles(overlay), h =>
            {
                double left = Canvas.GetLeft(h), top = Canvas.GetTop(h);
                bool outside = left + h.Width <= 0 || left >= skia.Width
                            || top + h.Height <= 0 || top >= skia.Height;
                Assert.True(outside, $"ручка {h.Tag} накрыла холст");
            });
        });
    }

    [Fact]
    // во время рисования ручки холста тоже прячутся
    public void canvas_handles_hide_while_drawing()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.ToolContext.IsDrawing = true;
            var (_, overlay) = Show(vm);
            Assert.Empty(CanvasHandles(overlay));
        });
    }

    [Fact]
    // ручки холста едут вместе с масштабом
    public void canvas_handles_follow_the_zoom()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel { Zoom = 2.0 };
            var (_, overlay) = Show(vm);
            var east = CanvasHandles(overlay).Single(h => ((CanvasEdgeTag)h.Tag!).Edge == ResizeHandle.E);
            Assert.True(Canvas.GetLeft(east) >= vm.Document.CanvasWidth * 2,
                "правая ручка осталась на месте масштаба 1:1");
        });
    }

    [Fact]
    // рамка будущего размера видна КАК РАЗ во время перетаскивания ручки
    public void resize_preview_shows_while_the_handle_is_dragged()
    {
        // Ручки на время жеста прячутся, и первая версия рисовала рамку вместе с ними:
        // показывать было нечего ровно в тот момент, ради которого она нужна.
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var (view, overlay) = Show(vm);
            Assert.Empty(Kids<TextBlock>(overlay));

            SetResizeState(view, ResizeHandle.W, 1000, 600, 100, 0);
            vm.ToolContext.IsDrawing = true;      // так и есть во время жеста
            Redraw(view);

            Assert.Empty(CanvasHandles(overlay));            // ручки спрятаны
            var preview = Rects(overlay).Single();           // рамка на месте
            Assert.Equal(1000, preview.Width, 3);
            Assert.Equal(600, preview.Height, 3);
            Assert.Equal(-100, Canvas.GetLeft(preview), 3);  // тянут влево - рамка влево
            Assert.False(preview.IsHitTestVisible, "рамка глотает мышь посреди жеста");

            var label = Kids<TextBlock>(overlay).Single();
            Assert.Contains("1000", label.Text);
            Assert.Contains("600", label.Text);
        });
    }

    /// <summary>
    /// Положить в вид состояние начатого растягивания. Настоящий жест не подделать:
    /// GetPosition у синтетического события отдаёт живое положение мыши, а не заданное.
    /// </summary>
    private static void SetResizeState(CanvasView view, ResizeHandle edge, int w, int h, int offX, int offY)
    {
        var type = typeof(CanvasView);
        var stateType = type.GetNestedType("CanvasResizeState",
            System.Reflection.BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(stateType,
            edge, w, h, new System.Windows.Point(0, 0), w, h, offX, offY);
        type.GetField("_canvasResize",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(view, state);
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
            var frame = Rects(overlay).Single();
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
            var frame = Rects(overlay).Single();
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
            Assert.False(Rects(overlay).Single().IsHitTestVisible);
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
            Assert.Equal(9, Rects(overlay).Count());
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
            Assert.Single(Rects(overlay));  // одна только рамка
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
            var skia = (FrameworkElement)view.FindName("Surface")!;
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
            var skia = (FrameworkElement)view.FindName("Surface")!;
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
            var skia = (FrameworkElement)view.FindName("Surface")!;
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
            var skia = (FrameworkElement)view.FindName("Surface")!;
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
