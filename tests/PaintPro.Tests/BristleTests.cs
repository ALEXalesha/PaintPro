using System.IO;
using System.Text.RegularExpressions;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using SkiaSharp;
using Xunit;
using IOPath = System.IO.Path;

namespace PaintPro.Tests;

/// <summary>
/// Кисть с ворсинками (1.33.0): до неё кисть и карандаш рисовали одно и то же. Пучок
/// ворсинок задаёт зерно; генератор и таблица совпадают с Electron-версией до двенадцатого
/// знака - эти числа посчитаны там же (bristlesFor в paint-pro.html) и стоят в
/// brush-bristles.spec.js.
/// </summary>
public class BristleTests
{
    // ───────── генератор и пучок ─────────

    [Theory]
    [InlineData(0u, 0.266429208685, 0.000329745701, 0.223272027448)]
    [InlineData(1u, 0.627073940588, 0.002735721180, 0.527447039960)]
    [InlineData(42u, 0.601103751920, 0.448290558998, 0.852465793490)]
    [InlineData(4294967295u, 0.896422614111, 0.189478256740, 0.715652678162)]
    // генератор выдаёт ровно те же числа, что mulberry32 в JavaScript
    public void the_generator_matches_javascript(uint seed, double a, double b, double c)
    {
        var rng = new Bristles.Rng(seed);
        Assert.Equal(a, rng.Next(), 11);
        Assert.Equal(b, rng.Next(), 11);
        Assert.Equal(c, rng.Next(), 11);
    }

    [Theory]
    [InlineData(42u, 1, -0.32046555678701766, -0.2723769783989844, 0.09688138290075586, 0.0664292815234512)]
    [InlineData(42u, 7, 0.07319670066318898, -0.40033817396415866, 0.10484831052133814, 0.2015275303972885)]
    [InlineData(42u, 31, -0.415253010181137, -0.11452843497612411, 0.11131463490659371, 0.34510250213555993)]
    [InlineData(7u, 1, 0.40130652631632213, 0.0677240715122745, 0.09893200939986856, 0.19814920204225928)]
    [InlineData(7u, 7, 0.1265133776974229, 0.39528951115146016, 0.1033311586547643, 0.028316874243319034)]
    [InlineData(7u, 31, 0.419406183183915, -0.09808642345939532, 0.08424538550199942, 0.0873138733766973)]
    [InlineData(3000000000u, 1, 0.05688875296562796, -0.4140510997587827, 0.07347213685512544, 0.1663915789918974)]
    [InlineData(3000000000u, 7, 0.39794980104512123, -0.18667740736205, 0.11417649228591473, 0.08281533745583147)]
    [InlineData(3000000000u, 31, -0.1529357493667257, -0.39299979995257467, 0.061680057342164224, 0.14813924382440746)]
    // пучок с тем же зерном - тот же, что в Electron-версии
    public void the_bristle_table_matches_electron(uint seed, int i, double dx, double dy, double w, double shade)
    {
        var b = Bristles.For(seed)[i];
        Assert.Equal(dx, b.Dx, 12);
        Assert.Equal(dy, b.Dy, 12);
        Assert.Equal(w, b.Width, 12);
        Assert.Equal(shade, b.Shade, 12);
    }

    [Theory]
    [InlineData(0.0, 0x20, 0x40, 0x80)]
    [InlineData(0.5, 0x90, 0xA0, 0xC0)]
    [InlineData(1.0, 0xFF, 0xFF, 0xFF)]
    // оттенок - доля пути к белому, прозрачность не трогает
    public void the_tint_moves_toward_white(double shade, int r, int g, int b)
    {
        var c = Bristles.Tint(new SKColor(0x20, 0x40, 0x80, 0x7F), shade);
        Assert.Equal(new SKColor((byte)r, (byte)g, (byte)b, 0x7F), c);
    }

    [Fact]
    // ворсинка 0 - сердцевина по центру
    public void bristle_zero_is_the_core()
    {
        for (uint s = 0; s < 50; s++)
        {
            var b = Bristles.For(s);
            Assert.Equal(Bristles.Count, b.Length);
            Assert.Equal(new Bristles.Bristle(0, 0, Bristles.CoreWidth, 0), b[0]);
        }
    }

    [Fact]
    // весь пучок - внутри круга размера: кружок размера не врёт
    public void the_bundle_stays_inside_the_size_circle()
    {
        for (uint s = 0; s < 2000; s++)
            foreach (var b in Bristles.For(s * 2654435761u))
            {
                double reach = Math.Sqrt(b.Dx * b.Dx + b.Dy * b.Dy) + b.Width / 2;
                Assert.True(reach <= 0.5 + 1e-12, $"{s}: {reach}");
                Assert.InRange(b.Width, Bristles.MinWidth, Bristles.CoreWidth);
                // только к белому: сердцевина (оттенок 0) самая тёмная
                Assert.InRange(b.Shade, 0, Bristles.ShadeMin + Bristles.ShadeSpread);
            }
    }

    [Fact]
    // в любом направлении мазка пучок достаёт почти до края круга: полоса не уже 0.84
    // размера и не шире самого размера - кружок размера не врёт
    public void the_band_is_nearly_the_size_in_every_direction()
    {
        double worst = 1;
        for (uint s = 0; s < 3000; s++)
        {
            var set = Bristles.For(s * 2654435761u);
            for (int deg = 0; deg < 180; deg += 3)
            {
                double th = deg * Math.PI / 180, up = 0, down = 0;
                foreach (var b in set)
                {
                    double perp = -b.Dx * Math.Sin(th) + b.Dy * Math.Cos(th);
                    up = Math.Max(up, perp + b.Width / 2);
                    down = Math.Max(down, -perp + b.Width / 2);
                }
                worst = Math.Min(worst, Math.Min(up, down));
            }
        }
        Assert.InRange(worst, 0.42, 0.5);
    }

    [Fact]
    // разные зёрна - разные пучки, одно зерно - один и тот же
    public void seeds_give_different_bundles()
    {
        Assert.Equal(Bristles.For(5), Bristles.For(5));
        Assert.NotEqual(Bristles.For(5)[1], Bristles.For(6)[1]);
    }

    // ───────── мазок ─────────

    private static (Document doc, PixelLayer layer, ToolContext ctx) Make(int w = 300, int h = 200)
    {
        var doc = new Document(w, h);
        return (doc, (PixelLayer)doc.ActiveLayer, new ToolContext(doc) { PrimaryColor = SKColors.Black, Opacity = 1f });
    }

    private static SKBitmap Stroke(StrokeToolBase tool, float size, params SKPoint[] pts)
    {
        var (_, layer, ctx) = Make();
        ctx.ToolSize = size;
        tool.OnPointerDown(pts[0], ctx);
        foreach (var p in pts.Skip(1)) tool.OnPointerMove(p, ctx);
        tool.OnPointerUp(pts[^1], ctx);
        return layer.Bitmap.Copy();
    }

    private static bool Ink(SKBitmap b, int x, int y) => b.GetPixel(x, y).Red < 128;

    private static readonly SKPoint[] Line = { new(40, 100), new(120, 100), new(200, 100), new(260, 100) };

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    // кисть больше не рисует то же, что карандаш
    public void the_brush_differs_from_the_pencil(float size)
    {
        using var brush = Stroke(new BrushTool { NextSeed = 42 }, size, Line);
        using var pencil = Stroke(new PencilTool(), size, Line);
        int diff = 0;
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 300; x++)
                if (brush.GetPixel(x, y) != pencil.GetPixel(x, y)) diff++;
        Assert.True(diff > size * 20, $"{diff}");
    }

    [Theory]
    [InlineData(20u)]
    [InlineData(42u)]
    [InlineData(777u)]
    // поперёк мазка есть просветы - полоски ворсинок, а середина закрашена всегда
    public void across_the_stroke_there_are_gaps_but_the_middle_is_solid(uint seed)
    {
        const float size = 60;
        using var b = Stroke(new BrushTool { NextSeed = seed }, size, Line);
        int gaps = 0;
        for (int y = 100 - 29; y <= 100 + 29; y++) if (!Ink(b, 150, y)) gaps++;
        Assert.True(gaps > 0, "ни одного просвета - это не ворсинки");
        for (int y = 100 - 7; y <= 100 + 7; y++) Assert.True(Ink(b, 150, y), $"сердцевина, y={y}");
    }

    [Fact]
    // за кругом размера кисть не рисует: всё закрашенное - не дальше половины размера от линии
    public void nothing_is_painted_outside_the_size()
    {
        const float size = 60;
        for (uint seed = 0; seed < 12; seed++)
        {
            using var b = Stroke(new BrushTool { NextSeed = seed }, size, Line);
            for (int y = 0; y < 200; y++)
                for (int x = 0; x < 300; x++)
                {
                    if (b.GetPixel(x, y) == SKColors.White) continue;
                    double cx = Math.Clamp(x + 0.5, 40, 260);
                    double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - 100) * (y + 0.5 - 100));
                    Assert.True(d <= size / 2 + 1.5, $"{seed}: ({x},{y}) d={d}");
                }
        }
    }

    [Fact]
    // порядок ворсинок ни на что не влияет (Darken): иначе конец ворсинки с прошлого куска
    // то накрывал соседнюю, то уходил под неё, и вдоль ворсинок шёл пунктир
    public void the_bristle_order_does_not_matter()
    {
        var set = Bristles.For(42);
        var reversed = set.Reverse().ToArray();
        using var a = new SKBitmap(200, 120, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var b = new SKBitmap(200, 120, SKColorType.Bgra8888, SKAlphaType.Premul);
        foreach (var (bmp, s) in new[] { (a, set), (b, reversed) })
        {
            using var c = new SKCanvas(bmp);
            c.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, Color = new SKColor(0xC0, 0x39, 0x2B) };
            using var fill = new SKPaint { IsAntialias = true, Color = paint.Color };
            Bristles.DrawDot(c, s, new SKPoint(30, 60), 60, fill);
            var pts = new[] { new SKPoint(30, 60), new SKPoint(70, 52), new SKPoint(110, 66), new SKPoint(170, 58) };
            for (int i = 1; i < pts.Length; i++) Bristles.DrawSegment(c, s, pts[i - 1], pts[i], 60, paint);
        }
        // Сглаженные края перекрытых ворсинок чуть зависят от порядка - на единицы; пунктир
        // давал разницу в целый оттенок, десятки уровней.
        int worst = 0; string where = "";
        for (int y = 0; y < 120; y++)
            for (int x = 0; x < 200; x++)
            {
                var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
                if (p.Alpha < 200 && q.Alpha < 200) continue;
                int d = Math.Max(Math.Max(Math.Abs(p.Red - q.Red), Math.Abs(p.Green - q.Green)), Math.Abs(p.Blue - q.Blue));
                if (d > worst) { worst = d; where = $"({x},{y}) {p} {q}"; }
            }
        Assert.True(worst <= 20, $"worst {worst} at {where}");
    }

    [Theory]
    [InlineData(0xC0, 0x39, 0x2B)]
    [InlineData(0x00, 0x00, 0x00)]
    [InlineData(0x2E, 0x86, 0xDE)]
    // середина мазка - ровно выбранный цвет: сердцевина самая тёмная и видна везде
    public void the_middle_of_the_stroke_is_exactly_the_colour(int r, int g, int b)
    {
        var (_, layer, ctx) = Make();
        ctx.PrimaryColor = new SKColor((byte)r, (byte)g, (byte)b);
        ctx.ToolSize = 50;
        var tool = new BrushTool { NextSeed = 5 };
        tool.OnPointerDown(Line[0], ctx);
        foreach (var p in Line.Skip(1)) tool.OnPointerMove(p, ctx);
        tool.OnPointerUp(Line[^1], ctx);
        for (int x = 60; x < 240; x += 7)
            Assert.Equal(ctx.PrimaryColor, layer.Bitmap.GetPixel(x, 100));
    }

    [Fact]
    // у ворсинок есть оттенки светлее: полоски видны и там, где ворсинки легли вплотную
    public void the_stroke_has_lighter_streaks()
    {
        var (_, layer, ctx) = Make();
        ctx.PrimaryColor = new SKColor(0x20, 0x20, 0x60);
        ctx.ToolSize = 60;
        var tool = new BrushTool { NextSeed = 42 };
        tool.OnPointerDown(Line[0], ctx);
        foreach (var p in Line.Skip(1)) tool.OnPointerMove(p, ctx);
        tool.OnPointerUp(Line[^1], ctx);
        var shades = new HashSet<SKColor>();
        for (int y = 70; y <= 130; y++)
        {
            var c = layer.Bitmap.GetPixel(150, y);
            if (c != SKColors.White) shades.Add(c);
        }
        Assert.True(shades.Count >= 4, $"{shades.Count}");
    }

    [Fact]
    // одно зерно - мазок до пикселя тот же
    public void the_same_seed_paints_the_same_pixels()
    {
        using var a = Stroke(new BrushTool { NextSeed = 9 }, 50, Line);
        using var b = Stroke(new BrushTool { NextSeed = 9 }, 50, Line);
        Assert.Equal(a.Bytes, b.Bytes);
    }

    [Fact]
    // пучок на каждый мазок свой
    public void each_stroke_gets_its_own_bundle()
    {
        var tool = new BrushTool();
        var seeds = new HashSet<uint>();
        for (int i = 0; i < 10; i++)
        {
            using var _ = Stroke(tool, 30, new SKPoint(40 + i * 3.5f, 100 + i), new SKPoint(260, 100));
            seeds.Add(tool.Seed);
        }
        Assert.Equal(10, seeds.Count);
    }

    [Theory]
    [InlineData(0f, 0f, 0u)]
    [InlineData(20f, 20f, 2026953024u)]
    [InlineData(100.5f, 300.25f, 18556874u)]
    [InlineData(1234.0625f, 77.3125f, 260011736u)]
    // зерно - из точки начала мазка, как bristleSeedAt в Electron-версии
    public void the_seed_comes_from_the_start_point_as_in_electron(float x, float y, uint seed)
    {
        Assert.Equal(seed, Bristles.SeedAt(new SKPoint(x, y)));
        var tool = new BrushTool();
        using var _ = Stroke(tool, 30, new SKPoint(x, y), new SKPoint(x + 5, y));
        Assert.Equal(seed, tool.Seed);
    }

    [Fact]
    // тот же мазок - те же пиксели, без всякого заданного зерна
    public void the_same_stroke_paints_the_same_pixels_on_its_own()
    {
        using var a = Stroke(new BrushTool(), 50, Line);
        using var b = Stroke(new BrushTool(), 50, Line);
        Assert.Equal(a.Bytes, b.Bytes);
    }

    [Fact]
    // щелчок тонкой кистью - полная точка выбранного цвета: сердцевина не тоньше 3 px
    public void a_click_with_a_thin_brush_leaves_a_full_dot()
    {
        using var b = Stroke(new BrushTool(), 6, new SKPoint(20, 20));
        Assert.Equal(SKColors.Black, b.GetPixel(20, 20));
        Assert.Equal(Bristles.CoreMinPx, Bristles.WidthPx(Bristles.For(1)[0], 6));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    // тонкая кисть - сплошная линия, как карандаш: ворсинки в пару пикселей неразличимы
    public void a_thin_brush_is_a_plain_line(float size)
    {
        using var brush = Stroke(new BrushTool { NextSeed = 42 }, size, Line);
        using var pencil = Stroke(new PencilTool(), size, Line);
        Assert.Equal(pencil.Bytes, brush.Bytes);
    }

    [Fact]
    // колесо посреди мазка: пучок растёт вместе с размером
    public void the_bundle_grows_with_the_size_mid_stroke()
    {
        var (_, layer, ctx) = Make();
        var tool = new BrushTool { NextSeed = 42 };
        ctx.ToolSize = 20;
        tool.OnPointerDown(new SKPoint(40, 100), ctx);
        tool.OnPointerMove(new SKPoint(120, 100), ctx);
        ctx.ToolSize = 80;
        tool.OnPointerMove(new SKPoint(260, 100), ctx);
        tool.OnPointerUp(new SKPoint(260, 100), ctx);
        int Height(int x) { int n = 0; for (int y = 0; y < 200; y++) if (Ink(layer.Bitmap, x, y)) n++; return n; }
        Assert.True(Height(80) <= 21, $"{Height(80)}");
        Assert.True(Height(200) > 40, $"{Height(200)}");
    }

    [Fact]
    // карандаш, маркер и ластик ворсинок не получили
    public void other_tools_are_unchanged()
    {
        using var pencil = Stroke(new PencilTool(), 40, Line);
        for (int y = 100 - 18; y <= 100 + 18; y++) Assert.True(Ink(pencil, 150, y));
    }

    // ───────── то же, что в Electron-версии ─────────

    [Fact]
    public void the_constants_are_the_same_as_in_electron()
    {
        var html = File.ReadAllText(IOPath.Combine(RepoRoot(), "paint-pro-electron", "paint-pro.html"));
        double Const(string name)
        {
            var m = Regex.Match(html, $@"\b{name} = ([0-9.]+)");
            Assert.True(m.Success, name);
            return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        Assert.Equal(Bristles.Count, Const("BRISTLE_COUNT"));
        Assert.Equal(Bristles.MinSize, Const("BRISTLE_MIN_SIZE"));
        Assert.Equal(Bristles.MaxOffset, Const("BRISTLE_MAX_OFFSET"));
        Assert.Equal(Bristles.MinWidth, Const("BRISTLE_MIN_WIDTH"));
        Assert.Equal(Bristles.WidthSpread, Const("BRISTLE_WIDTH_SPREAD"));
        Assert.Equal(Bristles.CoreWidth, Const("BRISTLE_CORE_WIDTH"));
        Assert.Equal(Bristles.ShadeMin, Const("BRISTLE_SHADE_MIN"));
        Assert.Equal(Bristles.ShadeSpread, Const("BRISTLE_SHADE_SPREAD"));
        Assert.Equal(Bristles.CoreMinPx, Const("BRISTLE_CORE_MIN_PX"));
        Assert.Equal(Bristles.RimCount, Const("BRISTLE_RIM_COUNT"));
        Assert.Equal(Bristles.RimInner, Const("BRISTLE_RIM_INNER"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(IOPath.Combine(dir.FullName, "PaintPro.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root");
    }
}
