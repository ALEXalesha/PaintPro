using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Xml.Linq;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.ViewModels;
using PaintPro.Views;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Почему рисование было медленным и что держит его быстрым (1.29.0).
///
/// Замер на настоящем окне: пока по холсту 900x600 водили кистью, он обновлялся 15-18 раз
/// в секунду. Причина - DropShadowEffect у стеклянных панелей: WPF размывал все тени окна
/// заново на каждом кадре, а панель с холстом вдобавок размывала вместе с тенью и сам
/// холст. Без теней то же окно давало 120 обновлений в секунду. Теперь тени лежат в
/// BitmapCache и размываются, только когда меняется то, что их отбрасывает, - те же 120.
///
/// Вторая, меньшая причина - растр холста собирался во всю поверхность, хотя в окне видна
/// её часть; на увеличении это восемь миллионов пикселей на кадр вместо полутора.
///
/// Скорость как таковую здесь не мерить: на раннере GitHub нет видеокарты, и числа там
/// ничего не значат. Проверяется устройство, из-за которого было медленно.
/// </summary>
public class RenderSpeedTests
{
    // ───────── разметка: каждая тень - под кэшем ─────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaintPro.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Src() => Path.Combine(RepoRoot(), "src", "PaintPro.Wpf");

    private static IEnumerable<string> XamlFiles()
        => Directory.EnumerateFiles(Src(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    public static IEnumerable<object[]> XamlFileNames()
        => XamlFiles().Select(f => new object[] { Path.GetRelativePath(Src(), f) });

    private static bool HasCacheMode(XElement e)
        => e.Attribute("CacheMode") is not null
           || e.Elements().Any(c => c.Name.LocalName.EndsWith(".CacheMode", StringComparison.Ordinal));

    /// <summary>Элементы, у которых задана тень: свойством-элементом или атрибутом.</summary>
    private static IEnumerable<XElement> EffectOwners(XDocument doc)
        => doc.Descendants()
            .Where(e => e.Name.LocalName.EndsWith(".Effect", StringComparison.Ordinal))
            .Select(e => e.Parent!)
            .Concat(doc.Descendants().Where(e => e.Attribute("Effect") is not null));

    [Theory]
    [MemberData(nameof(XamlFileNames))]
    // каждая тень в разметке лежит внутри элемента с BitmapCache
    public void every_effect_in_the_markup_sits_inside_a_bitmap_cache(string file)
    {
        var doc = XDocument.Load(Path.Combine(Src(), file));
        var bare = EffectOwners(doc)
            .Where(owner => !owner.Ancestors().Any(HasCacheMode))
            .Select(owner => $"{owner.Name.LocalName} {(string?)owner.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name")}")
            .ToList();
        Assert.True(bare.Count == 0, $"{file}: тень без кэша - {string.Join(", ", bare)}");
    }

    [Theory]
    [MemberData(nameof(XamlFileNames))]
    // тень не задаётся сеттером стиля: кэш вокруг такого элемента из стиля не проверить
    public void no_style_sets_an_effect(string file)
    {
        var doc = XDocument.Load(Path.Combine(Src(), file));
        var setters = doc.Descendants()
            .Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "Effect")
            .ToList();
        Assert.Empty(setters);
    }

    [Fact]
    // и из кода тени тоже не ставятся
    public void no_code_assigns_an_effect()
    {
        var hits = Directory.EnumerateFiles(Src(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .SelectMany(f => File.ReadLines(f).Select((l, i) => (f, l, i)))
            .Where(t => System.Text.RegularExpressions.Regex.IsMatch(t.l, @"\.Effect\s*=[^=]"))
            .Select(t => $"{Path.GetFileName(t.f)}:{t.i + 1}")
            .ToList();
        Assert.Empty(hits);
    }

    [Fact]
    // проверка разметки вообще что-то видит: теней в файлах много
    public void the_markup_check_finds_the_shadows_it_guards()
    {
        int total = XamlFiles().Sum(f => EffectOwners(XDocument.Load(f)).Count());
        Assert.True(total >= 12, $"найдено теней: {total}");
    }

    // ───────── внешний вид: тени остались те же ─────────

    private static DropShadowEffect Shadow(DependencyObject root)
        => Tree(root).OfType<UIElement>().Select(u => u.Effect).OfType<DropShadowEffect>().First();

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            foreach (var d in Tree(VisualTreeHelper.GetChild(root, i))) yield return d;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            yield return p;
    }

    private static ContentControl Panel(string style, UIElement content)
    {
        var cc = new ContentControl
        {
            Style = (Style)Application.Current.FindResource(style),
            Content = content,
            Padding = new Thickness(10),
        };
        cc.Measure(new Size(400, 300));
        cc.Arrange(new Rect(0, 0, 400, 300));
        cc.UpdateLayout();
        return cc;
    }

    [Theory]
    [InlineData("GlassPanel")]
    [InlineData("GlassPanelLive")]
    // у стеклянной панели прежняя тень: 32 размытия, 8 вниз, 0.35
    public void a_glass_panel_keeps_its_shadow(string style)
    {
        WpfRunner.Run(() =>
        {
            var s = Shadow(Panel(style, new TextBlock { Text = "x" }));
            Assert.Equal(32, s.BlurRadius);
            Assert.Equal(8, s.ShadowDepth);
            Assert.Equal(0.35, s.Opacity, 6);
            Assert.Equal(Colors.Black, s.Color);
        });
    }

    [Theory]
    [InlineData("GlassPanel")]
    [InlineData("GlassPanelLive")]
    // тень панели - под BitmapCache
    public void a_glass_panel_shadow_is_cached(string style)
    {
        WpfRunner.Run(() =>
        {
            var cc = Panel(style, new TextBlock { Text = "x" });
            var owner = Tree(cc).OfType<UIElement>().First(u => u.Effect is DropShadowEffect);
            Assert.Contains(Ancestors(owner).OfType<UIElement>(), a => a.CacheMode is BitmapCache);
        });
    }

    [Fact]
    // у «живой» панели содержимое вне тени и вне кэша: его перерисовка ничего не размывает
    public void the_live_panel_keeps_its_content_out_of_the_shadow_and_the_cache()
    {
        WpfRunner.Run(() =>
        {
            var content = new TextBlock { Text = "x" };
            Panel("GlassPanelLive", content);
            foreach (var a in Ancestors(content).OfType<UIElement>().TakeWhile(a => a is not ContentControl))
            {
                Assert.Null(a.Effect);
                Assert.Null(a.CacheMode);
            }
        });
    }

    [Fact]
    // у обычной панели содержимое внутри тени, как было: вид статичных панелей тот же
    public void the_static_panel_still_casts_the_shadow_of_its_content()
    {
        WpfRunner.Run(() =>
        {
            var content = new TextBlock { Text = "x" };
            Panel("GlassPanel", content);
            Assert.Contains(Ancestors(content).OfType<UIElement>(), a => a.Effect is DropShadowEffect);
        });
    }

    [Theory]
    [InlineData("GlassPanel")]
    [InlineData("GlassPanelLive")]
    // панель не ловит фокус клавиатуры: у Border его не было, у ContentControl есть по умолчанию
    public void a_glass_panel_takes_no_keyboard_focus(string style)
    {
        WpfRunner.Run(() =>
        {
            var cc = Panel(style, new TextBlock());
            Assert.False(cc.Focusable);
            Assert.False(cc.IsTabStop);
        });
    }

    [Theory]
    [InlineData("GlassPanel")]
    [InlineData("GlassPanelLive")]
    // отступ внутри панели прежний: рамка в 1 пиксель плюс Padding
    public void a_glass_panel_insets_its_content_by_the_border_and_padding(string style)
    {
        WpfRunner.Run(() =>
        {
            var content = new Border
            {
                Width = 50, Height = 20,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            var cc = Panel(style, content);
            var p = content.TranslatePoint(new Point(0, 0), cc);
            Assert.Equal(11, p.X, 3);
            Assert.Equal(11, p.Y, 3);
        });
    }

    [Theory]
    [InlineData("GlassPanel")]
    [InlineData("GlassPanelLive")]
    // пустое место панели ловит мышь, как ловил Border с фоном
    public void a_glass_panel_is_hit_testable_on_its_empty_area(string style)
    {
        WpfRunner.Run(() =>
        {
            var cc = Panel(style, new Border { Width = 10, Height = 10, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
            // VisualTreeHelper, а не InputHitTest: вне окна элемент считается невидимым, и
            // InputHitTest не находит ничего ни у какой панели.
            var hit = VisualTreeHelper.HitTest(cc, new Point(300, 200));
            Assert.NotNull(hit);
        });
    }

    // ───────── главное окно целиком ─────────

    /// <summary>
    /// Содержимое главного окна, разложенное без самого окна.
    ///
    /// Показывать MainWindow в тестах нельзя: окно с HWND, оставшееся жить на потоке
    /// WpfRunner, роняет хост тестов на выходе (UCEERR_RENDERTHREADFAILURE), а закрытое -
    /// записывает своё место в настройки пользователя. Поэтому содержимое вынимается из
    /// окна и раскладывается само, как CanvasView в соседних проверках: шаблоны и стили
    /// применяются при раскладке, окно для этого не нужно. Имена ищутся в окне - область
    /// имён осталась у него.
    /// </summary>
    private static (Window Window, FrameworkElement Root) MainWindowLaidOut()
    {
        var win = new MainWindow();
        var root = (FrameworkElement)win.Content;
        var dataContext = win.DataContext;
        win.Content = null;
        root.DataContext = dataContext;
        root.Measure(new Size(1600, 950));
        root.Arrange(new Rect(0, 0, 1600, 950));
        root.UpdateLayout();
        return (win, root);
    }

    [Fact]
    // в главном окне у каждой тени есть кэш сверху
    public void every_shadow_in_the_main_window_is_cached()
    {
        WpfRunner.Run(() =>
        {
            var (_, root) = MainWindowLaidOut();
            var owners = Tree(root).OfType<UIElement>().Where(u => u.Effect is not null).ToList();
            Assert.True(owners.Count >= 5, $"теней в окне: {owners.Count}");
            foreach (var o in owners)
                Assert.Contains(Ancestors(o).OfType<UIElement>(), a => a.CacheMode is BitmapCache);
        });
    }

    [Fact]
    // над растром холста нет ни тени, ни кэша: каждый его кадр не тянет за собой размытие
    public void nothing_above_the_canvas_raster_blurs_or_caches()
    {
        WpfRunner.Run(() =>
        {
            var (_, root) = MainWindowLaidOut();
            var skia = Tree(root).OfType<SkiaSharp.Views.WPF.SKElement>().Single();
            foreach (var a in Ancestors(skia).OfType<UIElement>())
            {
                Assert.Null(a.Effect);
                Assert.Null(a.CacheMode);
            }
        });
    }

    [Fact]
    // то же для строки состояния: координаты под курсором меняются 30 раз в секунду
    public void nothing_above_the_status_text_blurs_or_caches()
    {
        WpfRunner.Run(() =>
        {
            var (_, root) = MainWindowLaidOut();
            var zoomText = Tree(root).OfType<TextBlock>()
                .First(t => BindingOperationsPath(t) == "Zoom");
            foreach (var a in Ancestors(zoomText).OfType<UIElement>())
            {
                Assert.Null(a.Effect);
                Assert.Null(a.CacheMode);
            }
        });
    }

    private static string? BindingOperationsPath(TextBlock t)
        => System.Windows.Data.BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path?.Path;

    [Fact]
    // боковые панели остались со своими именами и шириной - код раскладки их находит
    public void the_side_panels_are_still_found_by_name()
    {
        WpfRunner.Run(() =>
        {
            var (win, _) = MainWindowLaidOut();
            var left = (FrameworkElement)win.FindName("LeftPanel")!;
            var right = (FrameworkElement)win.FindName("RightPanel")!;
            Assert.True(left.ActualWidth > 0);
            Assert.True(right.ActualWidth > 0);
            Assert.Equal("GlassPanel", StyleKey(win, left));
            Assert.Equal("GlassPanel", StyleKey(win, right));
        });
    }

    private static string? StyleKey(FrameworkElement owner, FrameworkElement el)
    {
        foreach (var key in new[] { "GlassPanel", "GlassPanelLive" })
            if (ReferenceEquals(el.Style, owner.TryFindResource(key))) return key;
        return null;
    }

    // ───────── тени под холстом ─────────

    private static (CanvasView View, FrameworkElement Skia, ScrollViewer Scroll) Show(MainViewModel vm, double w = 1200, double h = 900)
    {
        var view = new CanvasView { DataContext = vm };
        view.Measure(new Size(w, h));
        view.Arrange(new Rect(0, 0, w, h));
        view.UpdateLayout();
        return (view, (FrameworkElement)view.FindName("Skia")!, (ScrollViewer)view.FindName("Scroll")!);
    }

    [Fact]
    // под холстом те же две тени: вокруг (0.6) и со сдвигом вниз (0.35)
    public void the_canvas_keeps_both_of_its_shadows()
    {
        WpfRunner.Run(() =>
        {
            var (view, _, _) = Show(new MainViewModel());
            var frame = (UIElement)view.FindName("CanvasFrame")!;
            var drop = (UIElement)view.FindName("CanvasDropShadow")!;
            var f = Assert.IsType<DropShadowEffect>(frame.Effect);
            Assert.Equal((32.0, 0.0, 0.6), (f.BlurRadius, f.ShadowDepth, Math.Round(f.Opacity, 6)));
            var d = Assert.IsType<DropShadowEffect>(drop.Effect);
            Assert.Equal((32.0, 8.0, 0.35), (d.BlurRadius, d.ShadowDepth, Math.Round(d.Opacity, 6)));
        });
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    [InlineData(8.0)]
    // подложки теней того же размера и на том же месте, что растр
    public void the_shadow_casters_match_the_raster(double zoom)
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel { Zoom = zoom };
            var (view, skia, scroll) = Show(vm);
            foreach (var name in new[] { "CanvasFrame", "CanvasDropShadow" })
            {
                var el = (FrameworkElement)view.FindName(name)!;
                Assert.Equal(skia.ActualWidth, el.ActualWidth, 3);
                Assert.Equal(skia.ActualHeight, el.ActualHeight, 3);
                var a = el.TranslatePoint(new Point(0, 0), scroll);
                var b = skia.TranslatePoint(new Point(0, 0), scroll);
                Assert.Equal(b.X, a.X, 3);
                Assert.Equal(b.Y, a.Y, 3);
            }
        });
    }

    [Theory]
    [InlineData(900, 600, 1.0)]
    [InlineData(900, 600, 2.0)]
    [InlineData(900, 600, 4.0)]
    [InlineData(900, 600, 8.0)]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(1920, 1080, 4.0)]
    [InlineData(4000, 3000, 0.25)]
    [InlineData(4000, 3000, 1.0)]
    [InlineData(4000, 3000, 2.0)]
    // кэш теней не больше ShadowCacheSide по длинной стороне, мелкий - в полную величину
    public void the_shadow_cache_resolution_follows_the_surface(int w, int h, double zoom)
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            if (w != vm.Document.CanvasWidth || h != vm.Document.CanvasHeight) Assert.True(vm.ResizeCanvasTo(w, h, 0, 0));
            vm.Zoom = zoom;
            var (view, skia, _) = Show(vm);
            var cache = Assert.IsType<BitmapCache>(((UIElement)view.FindName("CanvasShadow")!).CacheMode);
            double side = Math.Max(skia.ActualWidth, skia.ActualHeight);
            Assert.Equal(ViewGeometry.ShadowCacheScale(skia.ActualWidth, skia.ActualHeight), cache.RenderAtScale, 9);
            Assert.True(side * cache.RenderAtScale <= ViewGeometry.ShadowCacheSide + 1e-6);
            if (side <= ViewGeometry.ShadowCacheSide) Assert.Equal(1.0, cache.RenderAtScale);
        });
    }

    [Theory]
    [InlineData(0, 0, 1.0)]
    [InlineData(100, 100, 1.0)]
    [InlineData(2048, 10, 1.0)]
    [InlineData(10, 2048, 1.0)]
    [InlineData(4096, 100, 0.5)]
    [InlineData(100, 8192, 0.25)]
    [InlineData(7200, 4800, 2048.0 / 7200)]
    [InlineData(double.NaN, 100, 1.0)]
    public void ShadowCacheScale_caps_the_long_side(double w, double h, double expected)
        => Assert.Equal(expected, ViewGeometry.ShadowCacheScale(w, h), 9);

    [Fact]
    // на всех разрешённых холстах и ступенях масштаба кэш теней влезает в текстуру видеокарты
    public void the_shadow_cache_fits_a_gpu_texture_for_every_allowed_zoom()
    {
        foreach (var (w, h) in new[] { (900, 600), (1920, 1080), (4000, 3000), (8000, 8000), (20000, 6000) })
        foreach (var z in GeometryMath.ZoomSteps)
        {
            if (z > ViewGeometry.LargestAllowedZoom(w, h) + 1e-9) continue;
            var (sw, sh) = ViewGeometry.SurfaceSize(w, h, z);
            double scale = ViewGeometry.ShadowCacheScale(sw, sh);
            Assert.InRange(scale, 0, 1);
            // поле тени - 32 размытия и 8 сдвига с каждой стороны
            Assert.True((Math.Max(sw, sh) + 80) * scale <= 4096, $"{w}x{h} x{z}: {Math.Max(sw, sh) * scale}");
        }
    }

    // ───────── только видимая часть растра ─────────

    [Fact]
    // холст целиком в окне - виден целиком
    public void VisibleSurfaceRect_whole_surface_when_it_fits()
    {
        var r = ViewGeometry.VisibleSurfaceRect(-100, -40, 1200, 900, 900, 600, 1.0, 900, 600);
        Assert.Equal(new SKRectI(0, 0, 900, 600), r);
    }

    [Fact]
    // прокрученный холст - видимая часть с запасом в два пикселя
    public void VisibleSurfaceRect_scrolled_part_with_padding()
    {
        var r = ViewGeometry.VisibleSurfaceRect(1000, 500, 1100, 800, 3600, 2400, 1.0, 3600, 2400);
        Assert.Equal(new SKRectI(998, 498, 2102, 1302), r);
    }

    [Fact]
    // масштаб экрана Windows переводит DIP в пиксели растра
    public void VisibleSurfaceRect_scales_to_device_pixels()
    {
        var r = ViewGeometry.VisibleSurfaceRect(100, 100, 400, 300, 1000, 1000, 1.5, 1500, 1500);
        Assert.Equal(new SKRectI(148, 148, 752, 602), r);
    }

    [Theory]
    [InlineData(-2000, 0, 1000, 800)]   // левее холста
    [InlineData(5000, 0, 1000, 800)]    // правее
    [InlineData(0, 3000, 1000, 800)]    // ниже
    [InlineData(0, 0, 0, 800)]          // окна нет
    [InlineData(0, 0, 1000, double.NaN)]
    public void VisibleSurfaceRect_nothing_visible_gives_null(double l, double t, double w, double h)
        => Assert.Null(ViewGeometry.VisibleSurfaceRect(l, t, w, h, 3600, 2400, 1.0, 3600, 2400));

    [Fact]
    // случайные окна: ответ всегда внутри растра и накрывает настоящую видимую часть
    public void VisibleSurfaceRect_always_covers_the_true_visible_part()
    {
        var rng = new Random(129);
        for (int i = 0; i < 2000; i++)
        {
            double sw = rng.Next(1, 5000), sh = rng.Next(1, 5000), scale = new[] { 1.0, 1.25, 1.5, 2.0 }[rng.Next(4)];
            int pw = (int)Math.Round(sw * scale), ph = (int)Math.Round(sh * scale);
            double l = rng.Next(-3000, 5000) + rng.NextDouble(), t = rng.Next(-3000, 5000) + rng.NextDouble();
            double w = rng.Next(1, 2500), h = rng.Next(1, 1500);
            var r = ViewGeometry.VisibleSurfaceRect(l, t, w, h, sw, sh, scale, pw, ph);

            double vl = Math.Max(0, l), vt = Math.Max(0, t), vr = Math.Min(sw, l + w), vb = Math.Min(sh, t + h);
            if (vr <= vl || vb <= vt) { Assert.Null(r); continue; }
            Assert.NotNull(r);
            var x = r!.Value;
            Assert.True(x.Left >= 0 && x.Top >= 0 && x.Right <= pw && x.Bottom <= ph, $"{x} вне {pw}x{ph}");
            Assert.True(x.Left <= Math.Floor(vl * scale) && x.Top <= Math.Floor(vt * scale), $"{x} не накрывает начало");
            Assert.True(x.Right >= Math.Min(pw, Math.Ceiling(vr * scale)) && x.Bottom >= Math.Min(ph, Math.Ceiling(vb * scale)), $"{x} не накрывает конец");
        }
    }

    /// <summary>Отрисовать вид в свою поверхность, как это делает SKElement.</summary>
    private static SKBitmap Paint(CanvasView view, int w, int h)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        bmp.Erase(SKColors.Magenta);
        using var surface = SKSurface.Create(bmp.Info, bmp.GetPixels(), bmp.RowBytes);
        typeof(CanvasView)
            .GetMethod("OnPaintSurface", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(view, new object?[] { null, new SKPaintSurfaceEventArgs(surface, bmp.Info) });
        surface.Canvas.Flush();
        return bmp;
    }

    private static MainViewModel RedCorner(double zoom)
    {
        var vm = new MainViewModel { Zoom = zoom };
        using var c = new SKCanvas(((PixelLayer)vm.Document.Layers[0]).Bitmap);
        c.DrawRect(new SKRect(0, 0, 300, 300), new SKPaint { Color = SKColors.Red });
        return vm;
    }

    [Fact]
    // на увеличении рисуется то, что в окне, а за краем окна растр не трогается
    public void only_the_visible_part_of_the_raster_is_painted()
    {
        WpfRunner.Run(() =>
        {
            var (view, skia, scroll) = Show(RedCorner(4.0));
            scroll.ScrollToHorizontalOffset(1000);
            scroll.ScrollToVerticalOffset(500);
            view.UpdateLayout();
            using var bmp = Paint(view, (int)skia.ActualWidth, (int)skia.ActualHeight);

            // В окне: документ (275, 150) - красный, (375, 250) - белая бумага.
            Assert.Equal(SKColors.Red, bmp.GetPixel(1100, 600));
            Assert.Equal(SKColors.White, bmp.GetPixel(1500, 1000));
            // Далеко за краем окна - то, что там было до отрисовки.
            Assert.Equal(SKColors.Magenta, bmp.GetPixel(3500, 2300));
            Assert.Equal(SKColors.Magenta, bmp.GetPixel(10, 10));
        });
    }

    [Fact]
    // прокрутили - в новом месте окна рисуется уже оно
    public void after_scrolling_the_newly_visible_part_is_painted()
    {
        WpfRunner.Run(() =>
        {
            var (view, skia, scroll) = Show(RedCorner(4.0));
            scroll.ScrollToHorizontalOffset(0);
            scroll.ScrollToVerticalOffset(0);
            view.UpdateLayout();
            using var first = Paint(view, (int)skia.ActualWidth, (int)skia.ActualHeight);
            Assert.Equal(SKColors.Red, first.GetPixel(10, 10));
            Assert.Equal(SKColors.Magenta, first.GetPixel(3500, 2300));

            scroll.ScrollToRightEnd();
            scroll.ScrollToBottom();
            view.UpdateLayout();
            using var second = Paint(view, (int)skia.ActualWidth, (int)skia.ActualHeight);
            Assert.Equal(SKColors.White, second.GetPixel(3500, 2300));
            Assert.Equal(SKColors.Magenta, second.GetPixel(10, 10));
        });
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    // холст целиком в окне - рисуется целиком, как раньше
    public void a_canvas_that_fits_is_painted_whole(double zoom)
    {
        WpfRunner.Run(() =>
        {
            var (v, skia, _) = Show(RedCorner(zoom));
            int w = (int)skia.ActualWidth, h = (int)skia.ActualHeight;
            using var bmp = Paint(v, w, h);
            Assert.Equal(SKColors.Red, bmp.GetPixel(0, 0));
            Assert.Equal(SKColors.White, bmp.GetPixel(w - 1, h - 1));
            Assert.Equal(SKColors.White, bmp.GetPixel(w - 1, 0));
            Assert.Equal(SKColors.White, bmp.GetPixel(0, h - 1));
        });
    }

    [Fact]
    // вид вне окна (так его рисует программа кадров) - растр собирается целиком
    public void a_view_without_a_viewport_paints_everything()
    {
        WpfRunner.Run(() =>
        {
            var view = new CanvasView { DataContext = RedCorner(1.0) };
            using var bmp = Paint(view, 900, 600);
            Assert.Equal(SKColors.Red, bmp.GetPixel(0, 0));
            Assert.Equal(SKColors.White, bmp.GetPixel(899, 599));
        });
    }

    [Fact]
    // окно просмотра стало больше - открывшаяся часть рисуется
    public void a_grown_view_paints_the_part_it_opened()
    {
        WpfRunner.Run(() =>
        {
            var (view, skia, _) = Show(RedCorner(4.0), 800, 600);
            using var small = Paint(view, (int)skia.ActualWidth, (int)skia.ActualHeight);
            Assert.Equal(SKColors.Magenta, small.GetPixel(1500, 1000));

            view.Measure(new Size(1800, 1300));
            view.Arrange(new Rect(0, 0, 1800, 1300));
            view.UpdateLayout();
            using var big = Paint(view, (int)skia.ActualWidth, (int)skia.ActualHeight);
            Assert.Equal(SKColors.White, big.GetPixel(1500, 1000));
        });
    }
}
