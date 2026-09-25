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

    /// <summary>Меньше этого холст не сжимается ручкой: за край надо чем-то ухватиться.</summary>
    public const int MinCanvasSide = 50;

    /// <summary>
    /// Новый размер холста и смещение рисунка при перетаскивании одной ручки.
    ///
    /// Стороны делятся на две породы. Правая и нижняя двигают только край: рисунок стоит
    /// на месте, смещение ноль. Левая и верхняя двигают НАЧАЛО холста, и рисунок обязан
    /// поехать вместе с ним - иначе «потянул влево» на экране выглядит как «рисунок
    /// прыгнул вправо», то есть жест сделал не то, что показывал. Вся разница между
    /// породами - в одном числе, которое здесь и считается.
    ///
    /// dm/dn - сдвиг мыши в пикселях ДОКУМЕНТА (экранный, делённый на масштаб).
    /// Возвращаемое смещение - куда в НОВОМ холсте попадёт прежний левый верхний угол;
    /// при сжатии оно отрицательное, и лишнее просто срезается.
    /// </summary>
    public static (int W, int H, int OffX, int OffY) CanvasResize(
        ResizeHandle edge, int startW, int startH, int dm, int dn, bool keepRatio)
    {
        bool west  = edge is ResizeHandle.W or ResizeHandle.NW or ResizeHandle.SW;
        bool east  = edge is ResizeHandle.E or ResizeHandle.NE or ResizeHandle.SE;
        bool north = edge is ResizeHandle.N or ResizeHandle.NW or ResizeHandle.NE;
        bool south = edge is ResizeHandle.S or ResizeHandle.SW or ResizeHandle.SE;
        bool corner = (west || east) && (north || south);

        int w = east ? startW + dm : west ? startW - dm : startW;
        int h = south ? startH + dn : north ? startH - dn : startH;
        w = Math.Max(MinCanvasSide, w);
        h = Math.Max(MinCanvasSide, h);

        // Shift на углу держит пропорции. На стороне держать нечего: сторона одна.
        if (keepRatio && corner && startH > 0)
        {
            double ratio = (double)startW / startH;
            if ((double)w / h > ratio) w = (int)Math.Round(h * ratio);
            else h = (int)Math.Round(w / ratio);
            w = Math.Max(MinCanvasSide, w);
            h = Math.Max(MinCanvasSide, h);
        }

        (w, h) = ClampCanvasSize(w, h);

        // Смещение считается ПОСЛЕ всех ограничений: упрись размер в потолок - смещение
        // обязано упереться вместе с ним, иначе рисунок уедет дальше, чем выросла бумага,
        // и часть его окажется за краем холста, потерянной без единого слова.
        return (w, h, west ? w - startW : 0, north ? h - startH : 0);
    }

    /// <summary>
    /// Ужать размер до того, что команда смены размера вообще разрешает. Пределы берутся
    /// у неё же: разойдись они - ручка предлагала бы размер, который потом не применится.
    /// </summary>
    public static (int W, int H) ClampCanvasSize(int w, int h)
    {
        int max = Commands.ResizeCanvasCommand.MaxDimension;
        w = Math.Clamp(w, MinCanvasSide, max);
        h = Math.Clamp(h, MinCanvasSide, max);
        long pixels = (long)w * h;
        if (pixels > Commands.ResizeCanvasCommand.MaxPixels)
        {
            double k = Math.Sqrt((double)Commands.ResizeCanvasCommand.MaxPixels / pixels);
            w = Math.Max(MinCanvasSide, (int)(w * k));
            h = Math.Max(MinCanvasSide, (int)(h * k));
        }
        return (w, h);
    }

    /// <summary>
    /// Место ручки холста в координатах наложения, в ЭКРАННЫХ пикселях.
    ///
    /// Ручки стоят ЗА краем холста, а не поперёк него: половина поперёк накрывала бы
    /// крайние пиксели рисунка и глотала бы клики, адресованные инструменту. Места
    /// хватает - вокруг поверхности лежит поле <see cref="CanvasMargin"/>.
    /// </summary>
    public static (double Left, double Top, double Width, double Height) CanvasHandleBox(
        ResizeHandle edge, double surfaceW, double surfaceH, double thickness, double length, double gap)
    {
        bool west  = edge is ResizeHandle.W or ResizeHandle.NW or ResizeHandle.SW;
        bool east  = edge is ResizeHandle.E or ResizeHandle.NE or ResizeHandle.SE;
        bool north = edge is ResizeHandle.N or ResizeHandle.NW or ResizeHandle.NE;
        bool south = edge is ResizeHandle.S or ResizeHandle.SW or ResizeHandle.SE;
        bool corner = (west || east) && (north || south);

        double w = corner ? thickness * 1.7 : (west || east) ? thickness : length;
        double h = corner ? thickness * 1.7 : (west || east) ? length : thickness;

        double left = west ? -(w + gap) : east ? surfaceW + gap : (surfaceW - w) / 2;
        double top  = north ? -(h + gap) : south ? surfaceH + gap : (surfaceH - h) / 2;
        return (left, top, w, h);
    }

    /// <summary>
    /// Наибольший масштаб - верхняя ступень лесенки, для любого холста.
    ///
    /// С 1.21.0 до 1.30.0 он зависел от размера холста: SKElement растрировал себя во всю
    /// поверхность «холст × масштаб», и фотография 4000x3000 на восьмикратном увеличении
    /// просила три гигабайта и роняла приложение. Потолок был 64 млн пикселей поверхности,
    /// и такой фотографии больше 200% не давали. С 1.30.0 растр размером с окно просмотра
    /// (CanvasView.PlaceRaster), масштаб памяти не просит, и потолок снят: у всех холстов
    /// до 800%, как в Electron-версии. Функция осталась, чтобы вызывающим не пришлось
    /// знать, от чего потолок зависит.
    /// </summary>
    public static double LargestAllowedZoom(int canvasWidth, int canvasHeight)
        => GeometryMath.ZoomSteps[^1];

    /// <summary>
    /// Размер поверхности холста на экране. Округляется до целых пикселей: дробный размер
    /// даёт замыленный <c>WriteableBitmap</c> и дрожание при прокрутке.
    /// </summary>
    public static (double Width, double Height) SurfaceSize(int canvasWidth, int canvasHeight, double zoom)
    {
        double z = Math.Max(zoom, MinZoom);
        return (Math.Round(canvasWidth * z), Math.Round(canvasHeight * z));
    }

    /// <summary>
    /// Наибольшая сторона кэша теней под холстом, в пикселях.
    ///
    /// Тени лежат в BitmapCache, чтобы не размываться на каждом кадре (1.29.0). Кэш во всю
    /// величину поверхности на восьмикратном увеличении - это до 64 млн пикселей, четверть
    /// гигабайта видеопамяти и сторона больше, чем умеет видеокарта. Тень и так размыта на
    /// 32 пикселя, и уменьшенная копия выглядит так же.
    /// </summary>
    public const double ShadowCacheSide = 2048;

    /// <summary>
    /// Во сколько раз уменьшить кэш теней, чтобы его длинная сторона не превысила
    /// <see cref="ShadowCacheSide"/>. Поверхность меньше - кэш в полную величину.
    /// </summary>
    public static double ShadowCacheScale(double surfaceWidth, double surfaceHeight)
    {
        double side = Math.Max(surfaceWidth, surfaceHeight);
        if (!(side > ShadowCacheSide)) return 1.0;
        return ShadowCacheSide / side;
    }

    /// <summary>
    /// Та часть поверхности холста, которую сейчас видно в окне, в пикселях поверхности
    /// (то есть уже в пикселях растра SKElement), с запасом в пару пикселей на округление.
    /// Null - ничего не видно.
    ///
    /// SKElement растрирует себя во всю величину, и до 1.29.0 документ каждый кадр
    /// собирался на всей поверхности, хотя видна из неё часть: картинка 1920x1080 на
    /// двукратном увеличении - это 8 млн пикселей на кадр при окне в полтора. Рисуется
    /// теперь только видимое; остальное досчитывается, когда до него докрутят.
    /// </summary>
    /// <param name="viewLeft">Левый край окна просмотра в координатах поверхности, DIP.</param>
    /// <param name="deviceScale">Пикселей растра на DIP: масштаб экрана Windows.</param>
    public static SKRectI? VisibleSurfaceRect(
        double viewLeft, double viewTop, double viewWidth, double viewHeight,
        double surfaceWidth, double surfaceHeight, double deviceScale, int pixelWidth, int pixelHeight)
    {
        if (!(viewWidth > 0) || !(viewHeight > 0) || !(deviceScale > 0)) return null;
        double left = Math.Max(0, viewLeft), top = Math.Max(0, viewTop);
        double right = Math.Min(surfaceWidth, viewLeft + viewWidth);
        double bottom = Math.Min(surfaceHeight, viewTop + viewHeight);
        if (right <= left || bottom <= top) return null;

        const int pad = 2;
        var r = new SKRectI(
            Math.Max(0, (int)Math.Floor(left * deviceScale) - pad),
            Math.Max(0, (int)Math.Floor(top * deviceScale) - pad),
            Math.Min(pixelWidth, (int)Math.Ceiling(right * deviceScale) + pad),
            Math.Min(pixelHeight, (int)Math.Ceiling(bottom * deviceScale) + pad));
        return r.Width > 0 && r.Height > 0 ? r : null;
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
