using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// Рисование битмапа без его копирования.
///
/// <c>SKCanvas.DrawBitmap</c> в SkiaSharp 2.88 на каждый вызов делает из изменяемого
/// битмапа картинку-копию: весь битмап, сколько бы из него ни попадало на экран. Холст
/// рисует каждый слой и превью штриха на каждом кадре, и у фотографии 4000x3000 это 48 МБ
/// копирования на слой: 9 мс вместо 0,4 мс на один только слой (замер 1.30.0). Здесь битмап
/// оборачивается картинкой, которая смотрит в его же пиксели. Это безопасно, пока битмап
/// жив и не меняется во время вызова - а вызов синхронный.
///
/// Рисовать битмап в самого себя этим нельзя: картинка и холст смотрели бы в одни пиксели.
/// </summary>
public static class SkiaNoCopy
{
    /// <summary>Битмап целиком, левым верхним углом в (x, y).</summary>
    public static void DrawBitmapNoCopy(this SKCanvas canvas, SKBitmap bitmap, float x, float y, SKPaint? paint = null)
    {
        using var image = Wrap(bitmap);
        if (image is not null) canvas.DrawImage(image, x, y, paint);
    }

    /// <summary>Битмап целиком, растянутый в прямоугольник <paramref name="dest"/>.</summary>
    public static void DrawBitmapNoCopy(this SKCanvas canvas, SKBitmap bitmap, SKRect dest, SKPaint? paint = null)
    {
        using var image = Wrap(bitmap);
        if (image is not null) canvas.DrawImage(image, dest, paint);
    }

    /// <summary>Часть битмапа <paramref name="source"/> в прямоугольник <paramref name="dest"/>.</summary>
    public static void DrawBitmapNoCopy(this SKCanvas canvas, SKBitmap bitmap, SKRect source, SKRect dest, SKPaint? paint = null)
    {
        using var image = Wrap(bitmap);
        if (image is not null) canvas.DrawImage(image, source, dest, paint);
    }

    /// <summary>
    /// Картинка поверх пикселей битмапа, без копии. Null - пикселей у битмапа нет (пустой
    /// битмап): рисовать нечего. Обычный DrawBitmap на таком падал ArgumentNullException.
    /// </summary>
    private static SKImage? Wrap(SKBitmap bitmap)
    {
        // Картинка держит адрес пикселей битмапа, а не сам SKPixmap: его можно отпустить сразу.
        using var pixmap = bitmap.PeekPixels();
        return pixmap is null ? null : SKImage.FromPixels(pixmap);
    }
}
