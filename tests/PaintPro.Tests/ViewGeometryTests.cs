using PaintPro.Models;
using PaintPro.Services;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Арифметика окна просмотра: размер поверхности под масштаб, перевод экранных координат в
/// документ, прокрутка при зуме и панорамировании, положение ручек.
///
/// Всё это жило в code-behind холста среди обработчиков мыши, и проверить его было нельзя
/// ничем. Слой представления - тысяча с лишним строк, и до 1.21.0 в нём не было ни одного
/// теста, при том что именно он решает, куда попадёт клик. Логика вынесена в
/// <see cref="ViewGeometry"/> тем же приёмом, каким из вьюмодели вынесены
/// <c>TryParseCanvasSize</c> и <c>PasteOrigin</c>.
/// </summary>
public class ViewGeometryTests
{
    private static FloatingPickup Lift(Document doc, SKRect r)
    {
        PickupOps.PromoteRect(doc, r);
        return doc.FloatingPickup!;
    }

    private static Document Painted(int w = 200, int h = 200)
    {
        var doc = new Document(w, h);
        using var c = new SKCanvas(((PixelLayer)doc.Layers[0]).Bitmap);
        c.DrawRect(new SKRect(20, 20, 120, 120), new SKPaint { Color = SKColors.Red });
        return doc;
    }

    // ───────── размер поверхности и перевод координат ─────────

    [Fact]
    // масштаб 1:1 даёт поверхность размером с холст
    public void surface_matches_the_canvas_at_one_to_one()
    {
        var (w, h) = ViewGeometry.SurfaceSize(900, 600, 1.0);
        Assert.Equal(900, w);
        Assert.Equal(600, h);
    }

    [Fact]
    // дробный масштаб округляется до целых пикселей
    public void fractional_zoom_rounds()
    {
        var (w, h) = ViewGeometry.SurfaceSize(100, 100, 0.67);
        Assert.Equal(Math.Round(67.0), w);
        Assert.Equal(Math.Round(67.0), h);
    }

    [Fact]
    // нулевой масштаб не схлопывает поверхность в ничто
    public void zero_zoom_does_not_collapse_the_surface()
    {
        var (w, h) = ViewGeometry.SurfaceSize(900, 600, 0);
        Assert.True(w > 0 && h > 0);
    }

    [Fact]
    // подложка шире поверхности ровно на два поля
    public void content_adds_two_margins()
    {
        var (cw, ch) = ViewGeometry.ContentSize(900, 600);
        Assert.Equal(900 + ViewGeometry.CanvasMargin * 2, cw);
        Assert.Equal(600 + ViewGeometry.CanvasMargin * 2, ch);
    }

    [Fact]
    // перевод экранной точки в документ и обратно сходится
    public void screen_to_document_round_trips()
    {
        var p = ViewGeometry.ToDocument(400, 300, 2.0);
        Assert.Equal(200f, p.X, 3);
        Assert.Equal(150f, p.Y, 3);
    }

    [Fact]
    // нулевой масштаб не даёт деления на ноль
    public void zero_zoom_does_not_divide_by_zero()
    {
        var p = ViewGeometry.ToDocument(10, 10, 0);
        Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y));
    }

    // ───────── зум колесом с фокусом на курсоре ─────────

    [Fact]
    // точка документа под курсором остаётся под ним при приближении
    public void zoom_in_keeps_the_point_under_the_cursor()
    {
        // Курсор стоит в точке (300, 200) окна просмотра, прокрутка 0, масштаб 1.
        double oldZoom = 1.0, cursorX = 300, cursorY = 200, scrollX = 0, scrollY = 0;
        // точка документа под курсором: (позиция в окне + прокрутка - поле) / масштаб
        double docX = (cursorX + scrollX - ViewGeometry.CanvasMargin) / oldZoom;
        double docY = (cursorY + scrollY - ViewGeometry.CanvasMargin) / oldZoom;

        double newZoom = 2.0;
        var (nx, ny) = ViewGeometry.ZoomFocusOffset(docX, docY, newZoom, cursorX, cursorY);

        // после прокрутки та же точка документа обязана оказаться под тем же курсором
        double backX = (cursorX + nx - ViewGeometry.CanvasMargin) / newZoom;
        double backY = (cursorY + ny - ViewGeometry.CanvasMargin) / newZoom;
        Assert.Equal(docX, backX, 6);
        Assert.Equal(docY, backY, 6);
    }

    [Fact]
    // то же при отдалении
    public void zoom_out_keeps_the_point_under_the_cursor()
    {
        double oldZoom = 4.0, cursorX = 250, cursorY = 180, scrollX = 700, scrollY = 400;
        double docX = (cursorX + scrollX - ViewGeometry.CanvasMargin) / oldZoom;
        double docY = (cursorY + scrollY - ViewGeometry.CanvasMargin) / oldZoom;

        double newZoom = 1.0;
        var (nx, ny) = ViewGeometry.ZoomFocusOffset(docX, docY, newZoom, cursorX, cursorY);

        double backX = (cursorX + nx - ViewGeometry.CanvasMargin) / newZoom;
        double backY = (cursorY + ny - ViewGeometry.CanvasMargin) / newZoom;
        Assert.Equal(docX, backX, 6);
        Assert.Equal(docY, backY, 6);
    }

    [Fact]
    // зум в левый верхний угол холста не уводит прокрутку в минус дальше поля
    public void zoom_at_the_top_left_stays_sane()
    {
        var (nx, ny) = ViewGeometry.ZoomFocusOffset(0, 0, 8.0, ViewGeometry.CanvasMargin, ViewGeometry.CanvasMargin);
        Assert.Equal(0, nx, 6);
        Assert.Equal(0, ny, 6);
    }

    // ───────── панорамирование ─────────

    [Fact]
    // тянем вправо - содержимое едет вправо, прокрутка влево
    public void dragging_right_scrolls_left()
    {
        var (x, _) = ViewGeometry.PanOffset(500, 500, cursorX: 120, cursorY: 0, anchorX: 100, anchorY: 0);
        Assert.Equal(480, x, 6);
    }

    [Fact]
    // жест по частям равен жесту целиком
    public void pan_is_additive()
    {
        double x = 500, y = 500;
        (x, y) = ViewGeometry.PanOffset(x, y, 110, 210, 100, 200);
        (x, y) = ViewGeometry.PanOffset(x, y, 130, 240, 110, 210);
        var (wholeX, wholeY) = ViewGeometry.PanOffset(500, 500, 130, 240, 100, 200);
        Assert.Equal(wholeX, x, 6);
        Assert.Equal(wholeY, y, 6);
    }

    // ───────── ручки ─────────

    [Fact]
    // ручка NW неповёрнутого объекта стоит в его левом верхнем углу
    public void nw_handle_sits_at_the_corner()
    {
        var doc = Painted();
        var fp = Lift(doc, new SKRect(20, 30, 120, 130));
        var p = ViewGeometry.ResizeHandleAnchor(fp, ResizeHandle.NW);
        Assert.Equal(20f, p.X, 3);
        Assert.Equal(30f, p.Y, 3);
    }

    [Fact]
    // поворот на 180° меняет NW и SE местами
    public void half_turn_swaps_nw_and_se()
    {
        var doc = Painted();
        var fp = Lift(doc, new SKRect(20, 30, 120, 130));
        fp.SetRotation(MathF.PI);
        var nw = ViewGeometry.ResizeHandleAnchor(fp, ResizeHandle.NW);
        Assert.Equal(120f, nw.X, 2);
        Assert.Equal(130f, nw.Y, 2);
    }

    [Fact]
    // ручка рисуется по центру своей точки
    public void handle_is_centred_on_its_point()
    {
        var (left, top) = ViewGeometry.HandleTopLeft(new SKPoint(100, 50), 2.0, 12);
        Assert.Equal(100 * 2 - 6, left, 6);
        Assert.Equal(50 * 2 - 6, top, 6);
    }

    [Fact]
    // ручка поворота стоит на 28 ЭКРАННЫХ пикселей выше на любом масштабе
    public void rotate_handle_offset_is_screen_sized()
    {
        var doc = Painted();
        var fp = Lift(doc, new SKRect(20, 30, 120, 130));
        foreach (var zoom in new[] { 0.25, 1.0, 4.0, 8.0 })
        {
            var p = ViewGeometry.RotateHandleAnchor(fp, zoom, 28);
            double screenGap = (fp.Y - p.Y) * zoom;
            Assert.Equal(28, screenGap, 3);
        }
    }

    [Fact]
    // ручка поворота стоит над серединой верхней стороны
    public void rotate_handle_is_centred_horizontally()
    {
        var doc = Painted();
        var fp = Lift(doc, new SKRect(20, 30, 120, 130));
        var p = ViewGeometry.RotateHandleAnchor(fp, 1.0, 28);
        Assert.Equal(fp.X + fp.Width / 2f, p.X, 3);
    }

    [Fact]
    // у повёрнутого объекта ручка поворота едет вместе с ним
    public void rotate_handle_follows_the_rotation()
    {
        var doc = Painted();
        var fp = Lift(doc, new SKRect(20, 30, 120, 130));
        fp.SetRotation(MathF.PI);
        var p = ViewGeometry.RotateHandleAnchor(fp, 1.0, 28);
        Assert.True(p.Y > fp.Y + fp.Height, "после разворота ручка обязана оказаться снизу");
    }

    [Fact]
    // ручка поворота не накрывает маленький объект
    public void rotate_handle_clears_a_tiny_object()
    {
        var doc = Painted();
        var fp = Lift(doc, new SKRect(20, 20, 28, 28));   // объект 8x8
        double zoom = 1.0;
        var p = ViewGeometry.RotateHandleAnchor(fp, zoom, 28);
        double handleBottom = p.Y * zoom + 14 / 2.0;
        double bodyTop = fp.Y * zoom;
        Assert.True(handleBottom < bodyTop, $"ручка поворота залезает на тело: {handleBottom} >= {bodyTop}");
    }

    // ───────── границы холста и статусбар ─────────

    [Fact]
    // правый и нижний края холсту уже не принадлежат
    public void canvas_bounds_exclude_the_far_edges()
    {
        Assert.True(ViewGeometry.IsOverCanvas(new SKPoint(0, 0), 100, 100));
        Assert.True(ViewGeometry.IsOverCanvas(new SKPoint(99.9f, 99.9f), 100, 100));
        Assert.False(ViewGeometry.IsOverCanvas(new SKPoint(100, 50), 100, 100));
        Assert.False(ViewGeometry.IsOverCanvas(new SKPoint(-0.1f, 50), 100, 100));
    }

    [Fact]
    // статусбар показывает прочерк за пределами холста
    public void status_shows_a_dash_outside()
    {
        Assert.Equal("—", ViewGeometry.PositionLabel(null));
        Assert.Equal("", ViewGeometry.HexLabel(null));
    }

    [Fact]
    // HEX пишется двумя знаками на канал в верхнем регистре
    public void hex_is_two_uppercase_digits()
    {
        Assert.Equal(" #0A0B0C", ViewGeometry.HexLabel(new SKColor(10, 11, 12)));
    }
}
