using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// Арифметика окна просмотра: размер поверхности под масштаб, перевод экранных координат в
/// документ, прокрутка при зуме и панорамировании, положение ручек на экране и строки
/// статусбара.
///
/// Всё это жило в <see cref="Views.CanvasView"/> прямо среди обработчиков мыши, и проверить
/// его было нельзя ничем: тесты не поднимают WPF-дерево. Слой представления - тысяча с
/// лишним строк, и до 1.21.0 в нём не было ни одного теста, при том что именно он решает,
/// куда попадёт клик. Тем же приёмом из вьюмодели вынесены
/// <see cref="ViewModels.MainViewModel.TryParseCanvasSize"/> и
/// <see cref="ViewModels.MainViewModel.PasteOrigin"/>.
///
/// Здесь только счёт: ни одного обращения к WPF, ни одного к состоянию элементов.
/// </summary>
public static class ViewGeometry
{
    /// <summary>Поле вокруг холста в ЭКРАННЫХ пикселях - на нём лежит тень и ручка поворота.</summary>
    public const double CanvasMargin = 40;

    /// <summary>Наименьший масштаб, на который вообще можно делить.</summary>
    public const double MinZoom = 0.01;

    /// <summary>
    /// Потолок на площадь ЭКРАННОЙ поверхности холста, в пикселях.
    ///
    /// SKElement растрирует себя целиком, в размер своего элемента, а размер этот -
    /// холст, умноженный на масштаб. Виртуализации по видимой области там нет: под
    /// поверхность заводится один <c>WriteableBitmap</c> на всю площадь, по четыре байта
    /// на пиксель. Потолок стоял только у самого документа - сторона до 20000 и до 120
    /// млн пикселей всего, - а произведение «документ × масштаб» не проверял никто.
    /// Обычная фотография 4000x3000 на восьмикратном увеличении просила без малого три
    /// гигабайта, самый большой разрешённый холст - двадцать девять: приложение падало
    /// нехваткой памяти прямо в отрисовке, унося несохранённый рисунок. Дотянуться до
    /// этого можно было четырьмя нажатиями Ctrl+= .
    ///
    /// 64 млн пикселей - это 256 МБ на поверхность. Холсту по умолчанию хватает на весь
    /// восьмикратный зум (900x600x8x8 - это 34 млн), обычному снимку экрана тоже.
    /// </summary>
    public const long MaxSurfacePixels = 64_000_000;

    /// <summary>
    /// Наибольший масштаб, при котором поверхность ещё влезает в потолок. Ответ снимается
    /// с той же лесенки ступеней, по которой ходят Ctrl+= и Ctrl+- : зажатый масштаб
    /// обязан оставаться ступенью, а не дробным числом между ними. Наименьшая ступень
    /// возвращается всегда - иначе большой документ нельзя было бы показать вовсе.
    /// </summary>
    public static double LargestAllowedZoom(int canvasWidth, int canvasHeight)
    {
        long area = (long)Math.Max(1, canvasWidth) * Math.Max(1, canvasHeight);
        var steps = GeometryMath.ZoomSteps;
        double best = steps[0];
        foreach (var step in steps)
        {
            if ((double)area * step * step > MaxSurfacePixels) break;
            best = step;
        }
        return best;
    }

    /// <summary>
    /// Размер поверхности холста на экране. Округляется до целых пикселей: дробный размер
    /// даёт замыленный <c>WriteableBitmap</c> и дрожание при прокрутке.
    /// </summary>
    public static (double Width, double Height) SurfaceSize(int canvasWidth, int canvasHeight, double zoom)
    {
        double z = Math.Max(zoom, MinZoom);
        return (Math.Round(canvasWidth * z), Math.Round(canvasHeight * z));
    }

    /// <summary>Размер подложки: поверхность плюс поле с каждой стороны.</summary>
    public static (double Width, double Height) ContentSize(double surfaceWidth, double surfaceHeight)
        => (surfaceWidth + CanvasMargin * 2, surfaceHeight + CanvasMargin * 2);

    /// <summary>Экранная точка внутри поверхности холста - в координаты документа.</summary>
    public static SKPoint ToDocument(double screenX, double screenY, double zoom)
    {
        double z = Math.Max(zoom, MinZoom);
        return new SKPoint((float)(screenX / z), (float)(screenY / z));
    }

    /// <summary>
    /// Прокрутка после зума колесом: точка документа под курсором обязана остаться под ним.
    ///
    /// <paramref name="docX"/>/<paramref name="docY"/> - точка документа, снятая ДО смены
    /// масштаба; <paramref name="cursorInViewport"/> - положение курсора относительно окна
    /// просмотра, оно от масштаба не зависит.
    /// </summary>
    public static (double X, double Y) ZoomFocusOffset(
        double docX, double docY, double newZoom, double cursorViewportX, double cursorViewportY)
    {
        double z = Math.Max(newZoom, MinZoom);
        return (docX * z + CanvasMargin - cursorViewportX,
                docY * z + CanvasMargin - cursorViewportY);
    }

    /// <summary>
    /// Прокрутка при панорамировании «Рукой». Считается в координатах ОКНА ПРОСМОТРА, а не
    /// документа: прокрутка двигает холст под курсором, и следующая разница, померенная в
    /// документе, частью отменяла бы предыдущую прокрутку - картинка дрожала бы.
    /// </summary>
    public static (double X, double Y) PanOffset(
        double currentX, double currentY,
        double cursorX, double cursorY, double anchorX, double anchorY)
        => (currentX - (cursorX - anchorX), currentY - (cursorY - anchorY));

    /// <summary>
    /// Мировая точка, в которой стоит ручка поворота: над серединой верхней стороны
    /// объекта, с ЭКРАННЫМ отступом. Отступ в пикселях документа ехал бы вместе с
    /// масштабом - на восьмикратном увеличении ручка улетала на четверть экрана вверх.
    /// </summary>
    public static SKPoint RotateHandleAnchor(FloatingPickup fp, double zoom, double screenOffset)
    {
        double offset = screenOffset / Math.Max(zoom, MinZoom);
        var local = new SKPoint(fp.X + fp.Width / 2f, fp.Y - (float)offset);
        return fp.Rotation == 0f ? local : GeometryMath.Rotate(local, fp.Center, fp.Rotation);
    }

    /// <summary>Мировая точка ручки масштабирования с учётом поворота объекта.</summary>
    public static SKPoint ResizeHandleAnchor(FloatingPickup fp, ResizeHandle handle)
    {
        var local = GeometryMath.LocalHandlePosition(fp.X, fp.Y, fp.Width, fp.Height, handle);
        return fp.Rotation == 0f ? local : GeometryMath.Rotate(local, fp.Center, fp.Rotation);
    }

    /// <summary>Левый верхний угол ручки на слое-подложке: она рисуется по центру своей точки.</summary>
    public static (double Left, double Top) HandleTopLeft(SKPoint world, double zoom, double size)
        => (world.X * zoom - size / 2, world.Y * zoom - size / 2);

    /// <summary>Стоит ли курсор над холстом. Правый и нижний края холсту уже не принадлежат.</summary>
    public static bool IsOverCanvas(SKPoint documentPoint, int canvasWidth, int canvasHeight)
        => documentPoint.X >= 0 && documentPoint.Y >= 0
           && documentPoint.X < canvasWidth && documentPoint.Y < canvasHeight;

    /// <summary>Координаты для статусбара, или прочерк, если курсор ушёл с холста.</summary>
    public static string PositionLabel(SKPoint? documentPoint)
        => documentPoint is { } p
            ? $"X: {(int)MathF.Floor(p.X)}, Y: {(int)MathF.Floor(p.Y)}"
            : "—";

    /// <summary>Цвет под курсором для статусбара, или пусто.</summary>
    public static string HexLabel(SKColor? color)
        => color is { } c ? $" #{c.Red:X2}{c.Green:X2}{c.Blue:X2}" : "";
}
