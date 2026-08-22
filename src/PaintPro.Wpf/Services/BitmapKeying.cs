using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// Helpers for turning a flat background colour into transparency.
///
/// The document's background layer is opaque white, so a rectangular selection
/// always captures the white around the drawn marks. When that selection is lifted
/// into a <see cref="Models.FloatingPickup"/> and moved, the white travels with it and
/// paints over whatever is underneath. Keying the background out fixes that: only the
/// drawn pixels remain opaque, the rest becomes transparent and composites cleanly.
/// </summary>
public static class BitmapKeying
{
    /// <summary>
    /// Return a copy of <paramref name="source"/> where the background AROUND the drawn
    /// marks is made fully transparent. The original bitmap is left untouched.
    ///
    /// Прозрачным становится только фон вокруг рисунка: заливка идёт от краёв битмапа
    /// внутрь и останавливается на первом пикселе, не похожем на фон. Проход «все белые
    /// пиксели подряд» был проще, но пробивал дыры в самой картинке - блик на фото,
    /// белая заливка фигуры, глаз на рисунке, - и сквозь них просвечивало то, что лежит
    /// ниже. В Electron-версии (<c>keyOutWhite</c>) это устроено так с самого начала.
    /// </summary>
    public static SKBitmap KeyOutBackground(SKBitmap source, SKColor background, int tolerance = 8)
    {
        // Copy into an unpremultiplied bitmap so channel values are the raw colour and
        // writing alpha=0 doesn't leave premultiplied colour residue behind.
        var dst = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(dst))
            canvas.DrawBitmap(source, 0, 0);

        int w = dst.Width, h = dst.Height;
        if (w == 0 || h == 0) return dst;

        var pixels = dst.Pixels; // SKColor[] copy, unpremultiplied
        byte br = background.Red, bg = background.Green, bb = background.Blue;

        var seen = new bool[w * h];
        var stack = new Stack<int>();
        // Стартуем со всей рамки битмапа: фон - это то, что связано с краем.
        for (int x = 0; x < w; x++) { stack.Push(x); stack.Push((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { stack.Push(y * w); stack.Push(y * w + w - 1); }

        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (seen[i]) continue;
            seen[i] = true;

            var p = pixels[i];
            if (p.Alpha == 0) continue; // уже прозрачный - дальше не идём
            if (!Near(p.Red, br, tolerance) ||
                !Near(p.Green, bg, tolerance) ||
                !Near(p.Blue, bb, tolerance)) continue; // край рисунка

            pixels[i] = SKColors.Transparent;

            int x = i % w, y = i / w;
            if (x > 0)     stack.Push(i - 1);
            if (x < w - 1) stack.Push(i + 1);
            if (y > 0)     stack.Push(i - w);
            if (y < h - 1) stack.Push(i + w);
        }

        dst.Pixels = pixels;
        return dst;
    }

    private static bool Near(byte a, byte b, int tolerance) => Math.Abs(a - b) <= tolerance;
}
