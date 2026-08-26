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

        // Карта пройденного - битами, а не байтами: на большом холсте это разница между
        // одним байтом и одним битом на пиксель, то есть между 120 и 15 мегабайтами.
        var seen = new Bitmap1(w * h);
        var stack = new Stack<int>();

        // Пиксель помечается пройденным В МОМЕНТ ПОМЕЩЕНИЯ в стек, а не когда до него
        // дойдёт очередь. Пока метка ставилась при извлечении, один и тот же пиксель
        // попадал в стек столько раз, сколько у него соседей: стек рос до нескольких
        // размеров самой картинки, и подъём выделения во весь холст просил пять с
        // половиной его объёмов. Обход от этого не меняется ни на пиксель - каждый индекс
        // всё равно обрабатывается ровно один раз, - зато стек больше не может стать
        // длиннее, чем есть пикселей.
        void Push(int i) { if (!seen[i]) { seen[i] = true; stack.Push(i); } }

        // Стартуем со всей рамки битмапа: фон - это то, что связано с краем.
        for (int x = 0; x < w; x++) { Push(x); Push((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Push(y * w); Push(y * w + w - 1); }

        while (stack.Count > 0)
        {
            int i = stack.Pop();

            var p = pixels[i];
            if (p.Alpha == 0) continue; // уже прозрачный - дальше не идём
            if (!Near(p.Red, br, tolerance) ||
                !Near(p.Green, bg, tolerance) ||
                !Near(p.Blue, bb, tolerance)) continue; // край рисунка

            pixels[i] = SKColors.Transparent;

            int x = i % w, y = i / w;
            if (x > 0)     Push(i - 1);
            if (x < w - 1) Push(i + 1);
            if (y > 0)     Push(i - w);
            if (y < h - 1) Push(i + w);
        }

        dst.Pixels = pixels;
        return dst;
    }

    private static bool Near(byte a, byte b, int tolerance) => Math.Abs(a - b) <= tolerance;

    /// <summary>
    /// Карта «этот пиксель уже смотрели», по биту на пиксель.
    ///
    /// <c>bool[]</c> тратит на то же самое целый байт: на холсте в 120 млн пикселей это
    /// 120 МБ вместо 15. Обходы по всей площади есть и здесь, и в заливке
    /// (<see cref="Commands.FillCommand"/>), и обоим этот массив нужен размером с картинку.
    /// </summary>
    internal sealed class Bitmap1
    {
        private readonly ulong[] _bits;
        public Bitmap1(int count) => _bits = new ulong[(count + 63) / 64];

        public bool this[int i]
        {
            get => (_bits[i >> 6] & (1UL << (i & 63))) != 0;
            set
            {
                if (value) _bits[i >> 6] |= 1UL << (i & 63);
                else _bits[i >> 6] &= ~(1UL << (i & 63));
            }
        }
    }
}
