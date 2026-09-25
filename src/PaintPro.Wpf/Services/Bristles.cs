using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// Ворсинки кисти (1.33.0). До того кисть рисовала ровно то же, что карандаш: круглую
/// сплошную линию той же толщины, и разницы между ними не было никакой.
///
/// Теперь мазок кисти - пучок тонких линий-ворсинок. У каждой свой сдвиг от центра и своя
/// толщина, в долях размера, и свой оттенок - чуть светлее выбранного цвета, поэтому вдоль
/// мазка идут полоски, а край неровный. Пучок
/// держится весь мазок (кисть не вертят в руке) и растёт вместе с размером, если его
/// поменять колесом посреди мазка. Ворсинка 0 - сердцевина по центру ровно выбранного
/// цвета: середина мазка всегда закрашена и всегда тем цветом, что выбран.
///
/// Ворсинки смешиваются режимом Darken: где их несколько, остаётся самая тёмная. Мазок
/// рисуется кусками, и круглый конец ворсинки с прошлого куска выступает в следующий: при
/// обычном наложении он то накрывал соседнюю ворсинку, то уходил под неё, и вдоль ворсинок
/// шёл пунктир - при любом порядке рисования. С Darken порядок ни на что не влияет. Оттенки
/// поэтому только к белому: сердцевина самая тёмная и видна везде, где лежит. Буфер у мазка
/// свой, так что смешение касается только ворсинок этого мазка, а не слоя под ним. Весь пучок - внутри круга размера,
/// так что кружок размера по-прежнему не врёт.
///
/// Пучок задаётся числом-зерном, а зерно - точкой, где мазок начат (SeedAt): один и тот же
/// мазок всегда ложится одинаково, и повторить его можно без записи лишнего. Генератор
/// (mulberry32), зерно и порядок чисел те же, что в Electron-версии (bristlesFor,
/// bristleSeedAt): одно зерно даёт в обеих один и тот же пучок - это держит BristleTests и
/// brush-bristles.spec.js одной таблицей.
/// </summary>
public static class Bristles
{
    /// <summary>Сколько ворсинок в пучке, с сердцевиной.</summary>
    public const int Count = 32;

    /// <summary>Меньше этого размера ворсинки неразличимы - кисть рисует сплошной линией.</summary>
    public const float MinSize = 6;

    /// <summary>Дальше этого от центра (в долях размера) ворсинка не стоит.</summary>
    public const double MaxOffset = 0.44;

    /// <summary>
    /// Ворсинки по краю пучка: у каждой нечётной, от RimInner до 1 в долях MaxOffset,
    /// равномерно по кругу. Без них случайные ворсинки в каком-то направлении не доставали
    /// до края, и полоса выходила заметно уже кружка размера - кружок снова врал бы.
    /// </summary>
    public const int RimCount = 16;
    public const double RimInner = 0.92;

    /// <summary>Толщина ворсинки в долях размера: от MinWidth до MinWidth + WidthSpread.</summary>
    public const double MinWidth = 0.05, WidthSpread = 0.07;

    /// <summary>Толщина сердцевины в долях размера.</summary>
    public const double CoreWidth = 0.20;

    /// <summary>
    /// Сердцевина не тоньше этого, в пикселях: у тонкой кисти пятая часть размера - меньше
    /// пикселя, и точка от щелчка выходила бледной, а середина мазка - не того цвета.
    /// </summary>
    public const float CoreMinPx = 3;

    /// <summary>Оттенок ворсинки - доля пути к белому: от ShadeMin до ShadeMin + ShadeSpread.</summary>
    public const double ShadeMin = 0.0, ShadeSpread = 0.38;

    /// <summary>
    /// Ворсинка: сдвиг от центра мазка и толщина в долях размера кисти, оттенок - доля
    /// пути к белому.
    /// </summary>
    public readonly record struct Bristle(double Dx, double Dy, double Width, double Shade);

    /// <summary>mulberry32: 32-битный генератор, который в JavaScript пишется так же коротко.</summary>
    public sealed class Rng
    {
        private uint _a;
        public Rng(uint seed) => _a = seed;

        public double Next()
        {
            unchecked
            {
                _a += 0x6D2B79F5;
                uint t = _a;
                t = (t ^ (t >> 15)) * (1 | t);
                t = (t + (t ^ (t >> 7)) * (61 | t)) ^ t;
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }
    }

    /// <summary>
    /// Зерно мазка из точки, где он начат, с шагом в восьмую пикселя. Целочисленная
    /// арифметика с переполнением - та же, что Math.imul в JavaScript.
    /// </summary>
    public static uint SeedAt(SKPoint p)
    {
        unchecked
        {
            // Floor(v + 0.5) - как Math.round в JavaScript, в том числе для половинок.
            int ix = (int)Math.Floor(p.X * 8.0 + 0.5);
            int iy = (int)Math.Floor(p.Y * 8.0 + 0.5);
            return (uint)(ix * 73856093) ^ (uint)(iy * 19349663);
        }
    }

    /// <summary>Пучок для этого зерна: сердцевина и Count - 1 ворсинок, равномерно по кругу.</summary>
    public static Bristle[] For(uint seed)
    {
        var rng = new Rng(seed);
        var result = new Bristle[Count];
        result[0] = new Bristle(0, 0, CoreWidth, 0);
        double turn = rng.Next() * Math.PI * 2;   // поворот всего края: у каждого мазка свой
        for (int i = 1; i < Count; i++)
        {
            double u = rng.Next(), a = rng.Next();
            double r, angle;
            if (i % 2 == 1)
            {
                // Край: свой сектор круга, внутри сектора - со сдвигом не больше четверти.
                int k = (i - 1) / 2;
                angle = turn + (k + (a - 0.5) * 0.5) * (Math.PI * 2 / RimCount);
                r = MaxOffset * (RimInner + (1 - RimInner) * u);
            }
            else
            {
                // Середина: sqrt - чтобы по площади круга легли равномерно, а не у центра.
                angle = a * Math.PI * 2;
                r = MaxOffset * Math.Sqrt(u);
            }
            double w = MinWidth + WidthSpread * rng.Next();
            double shade = ShadeMin + ShadeSpread * rng.Next();
            result[i] = new Bristle(r * Math.Cos(angle), r * Math.Sin(angle), w, shade);
        }
        return result;
    }

    /// <summary>Кисть такого размера рисует ворсинками, а не сплошной линией.</summary>
    public static bool Applies(float size) => size >= MinSize;

    /// <summary>Толщина ворсинки в пикселях при таком размере: не тоньше пикселя, сердцевина - не тоньше CoreMinPx.</summary>
    public static float WidthPx(Bristle b, float size)
        => MathF.Max(b.Dx == 0 && b.Dy == 0 ? CoreMinPx : 1f, (float)(b.Width * size));

    /// <summary>Цвет ворсинки: выбранный, сдвинутый к белому на её оттенок.</summary>
    public static SKColor Tint(SKColor c, double shade)
    {
        byte Ch(byte v) => (byte)Math.Round(v + (255 - v) * shade, MidpointRounding.AwayFromZero);
        return new SKColor(Ch(c.Red), Ch(c.Green), Ch(c.Blue), c.Alpha);
    }

    /// <summary>Отрезок мазка: каждая ворсинка - свой отрезок, со своим сдвигом и оттенком.</summary>
    public static void DrawSegment(SKCanvas canvas, Bristle[] set, SKPoint a, SKPoint b, float size, SKPaint paint)
    {
        float savedWidth = paint.StrokeWidth;
        var savedColor = paint.Color;
        var savedBlend = paint.BlendMode;
        paint.BlendMode = SKBlendMode.Darken;
        foreach (var br in set)
        {
            float dx = (float)(br.Dx * size), dy = (float)(br.Dy * size);
            paint.StrokeWidth = WidthPx(br, size);
            paint.Color = Tint(savedColor, br.Shade);
            canvas.DrawLine(a.X + dx, a.Y + dy, b.X + dx, b.Y + dy, paint);
        }
        paint.StrokeWidth = savedWidth;
        paint.Color = savedColor;
        paint.BlendMode = savedBlend;
    }

    /// <summary>Отпечаток на нажатии: точка каждой ворсинки.</summary>
    public static void DrawDot(SKCanvas canvas, Bristle[] set, SKPoint p, float size, SKPaint fill)
    {
        var savedColor = fill.Color;
        var savedBlend = fill.BlendMode;
        fill.BlendMode = SKBlendMode.Darken;
        foreach (var br in set)
        {
            fill.Color = Tint(savedColor, br.Shade);
            canvas.DrawCircle(p.X + (float)(br.Dx * size), p.Y + (float)(br.Dy * size), WidthPx(br, size) / 2f, fill);
        }
        fill.Color = savedColor;
        fill.BlendMode = savedBlend;
    }
}
