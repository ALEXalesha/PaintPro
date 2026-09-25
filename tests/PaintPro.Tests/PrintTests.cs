using System.IO;
using System.IO.Packaging;
using System.Printing;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;
using IOPath = System.IO.Path;

namespace PaintPro.Tests;

/// <summary>
/// Печать листа (1.35.0): Ctrl+P и «Файл → Печать…». До этого печати в C#-версии не было
/// вовсе. Печатается одна картинка - как на экране, со слоями и объектом в руках, - в
/// той же раскладке, что в Electron-версии: поля 10 мм, в своём размере или меньше до
/// страницы, по центру по ширине, от верха.
/// </summary>
public class PrintTests
{
    // A4 в точках WPF
    private const double A4W = 793.7, A4H = 1122.5;

    // ───────── раскладка на странице ─────────

    [Fact]
    // маленькая картинка не растягивается: пиксель холста - точка
    public void a_small_picture_keeps_its_size()
    {
        var r = PrintService.FitOnPage(300, 200, A4W, A4H);
        Assert.Equal(300, r.Width, 6);
        Assert.Equal(200, r.Height, 6);
        Assert.Equal(PrintService.MarginDip, r.Y, 6);
        Assert.Equal(A4W / 2, r.X + r.Width / 2, 6);   // по центру по ширине
    }

    [Theory]
    [InlineData(900, 600)]
    [InlineData(4000, 3000)]
    [InlineData(600, 5000)]
    [InlineData(20000, 300)]
    // большая уменьшается вся, с сохранением пропорций, и целиком внутри полей
    public void a_big_picture_shrinks_to_the_page_keeping_its_shape(int w, int h)
    {
        var r = PrintService.FitOnPage(w, h, A4W, A4H);
        double m = PrintService.MarginDip;
        Assert.Equal((double)w / h, r.Width / r.Height, 6);
        Assert.True(r.Left >= m - 1e-9 && r.Top >= m - 1e-9);
        Assert.True(r.Right <= A4W - m + 1e-9 && r.Bottom <= A4H - m + 1e-9);
        // и упирается хотя бы в одну сторону - не мельче, чем нужно
        Assert.True(Math.Abs(r.Width - (A4W - 2 * m)) < 1e-6 || Math.Abs(r.Height - (A4H - 2 * m)) < 1e-6);
    }

    [Fact]
    // поля - 10 мм, как @page в Electron-версии
    public void the_margin_is_ten_millimetres_as_in_electron()
    {
        Assert.Equal(37.795, PrintService.MarginDip, 3);
        var html = File.ReadAllText(IOPath.Combine(RepoRoot(), "paint-pro-electron", "paint-pro.html"));
        Assert.Matches(new Regex(@"@page\s*\{\s*margin:\s*10mm;"), html);
    }

    [Theory]
    [InlineData(900, 600, PageOrientation.Landscape)]
    [InlineData(600, 900, PageOrientation.Portrait)]
    [InlineData(500, 500, PageOrientation.Portrait)]
    // ориентация подсказывается по форме картинки
    public void the_orientation_follows_the_picture(int w, int h, PageOrientation want)
        => Assert.Equal(want, PrintService.PreferredOrientation(w, h));

    // ───────── картинка и документ ─────────

    [Fact]
    // картинка для печати - те же пиксели, что у холста
    public void the_print_image_has_the_canvas_pixels()
    {
        using var bmp = new SKBitmap(40, 30, SKColorType.Bgra8888, SKAlphaType.Premul);
        bmp.Erase(SKColors.White);
        bmp.SetPixel(10, 5, SKColors.Red);
        bmp.SetPixel(39, 29, new SKColor(0x20, 0x40, 0x80));
        var src = PrintService.ToBitmapSource(bmp);
        Assert.Equal(40, src.PixelWidth);
        Assert.Equal(30, src.PixelHeight);
        Assert.True(src.IsFrozen);
        var px = new byte[4];
        src.CopyPixels(new Int32Rect(10, 5, 1, 1), px, 4, 0);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, px);        // BGRA красный
        src.CopyPixels(new Int32Rect(39, 29, 1, 1), px, 4, 0);
        Assert.Equal(new byte[] { 0x80, 0x40, 0x20, 255 }, px);
    }

    [Fact]
    // документ - одна страница, на ней одна картинка там, где сказала раскладка
    public void the_document_is_one_page_with_the_picture_placed()
    {
        WpfRunner.Run(() =>
        {
            using var bmp = new SKBitmap(1200, 800);
            bmp.Erase(SKColors.White);
            var doc = PrintService.BuildDocument(PrintService.ToBitmapSource(bmp), new Size(A4W, A4H));
            Assert.Single(doc.Pages);
            var page = doc.Pages[0].Child;
            Assert.Equal(A4W, page.Width, 6);
            var img = Assert.IsType<Image>(Assert.Single(page.Children.Cast<UIElement>()));
            var want = PrintService.FitOnPage(1200, 800, A4W, A4H);
            Assert.Equal(want.X, FixedPage.GetLeft(img), 6);
            Assert.Equal(want.Y, FixedPage.GetTop(img), 6);
            Assert.Equal(want.Width, img.Width, 6);
            Assert.Equal(want.Height, img.Height, 6);
        });
    }

    [Fact]
    // и принтер его принимает: запись в XPS - тот же путь, каким документ идёт на принтер
    public void the_document_goes_through_the_print_path()
    {
        WpfRunner.Run(() =>
        {
            using var bmp = new SKBitmap(900, 600);
            bmp.Erase(SKColors.White);
            var doc = PrintService.BuildDocument(PrintService.ToBitmapSource(bmp), new Size(A4H, A4W));
            var file = IOPath.Combine(IOPath.GetTempPath(), $"paintpro-print-{Guid.NewGuid():N}.xps");
            try
            {
                using (var xps = new XpsDocument(file, FileAccess.ReadWrite))
                    XpsDocument.CreateXpsDocumentWriter(xps).Write(doc.DocumentPaginator);
                using (var read = new XpsDocument(file, FileAccess.Read))
                    Assert.Equal(1, read.GetFixedDocumentSequence().DocumentPaginator.PageCount);
                using (var pkg = Package.Open(file, FileMode.Open, FileAccess.Read))
                    Assert.Contains(pkg.GetParts(), p => p.ContentType.StartsWith("image/"));
            }
            finally { File.Delete(file); }
        });
    }

    // ───────── команда ─────────

    private static (MainViewModel vm, List<BitmapSource> printed) Vm(bool accept = true)
    {
        var vm = new MainViewModel();
        var printed = new List<BitmapSource>();
        vm.Printer = img => { printed.Add(img); return accept; };
        return (vm, printed);
    }

    private static void Rect(MainViewModel vm, SKColor c, float x0, float y0, float x1, float y1)
    {
        vm.ActiveTool = ToolKind.Rect;
        vm.ToolContext.PrimaryColor = c;
        var t = vm.ActiveToolInstance;
        ((PaintPro.Tools.ShapeTool)t).Fill = true;
        t.OnPointerDown(new SKPoint(x0, y0), vm.ToolContext);
        t.OnPointerMove(new SKPoint(x1, y1), vm.ToolContext);
        t.OnPointerUp(new SKPoint(x1, y1), vm.ToolContext);
    }

    private static byte[] Px(BitmapSource s, int x, int y)
    {
        var px = new byte[4];
        s.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return px;
    }

    [Fact]
    // Ctrl+P печатает холст в его размер, как он есть
    public void print_sends_the_canvas_as_it_is()
    {
        WpfRunner.Run(() =>
        {
            var (vm, printed) = Vm();
            Rect(vm, SKColors.Red, 100, 100, 300, 250);
            vm.PrintCommand.Execute(null);
            var s = Assert.Single(printed);
            Assert.Equal(vm.Document.CanvasWidth, s.PixelWidth);
            Assert.Equal(vm.Document.CanvasHeight, s.PixelHeight);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Px(s, 200, 180));
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, Px(s, 600, 400));
        });
    }

    [Fact]
    // объект в руках печатается там, где лежит, а документ не меняется: объект так и в руках
    public void a_held_object_is_printed_where_it_lies_and_stays_held()
    {
        WpfRunner.Run(() =>
        {
            var (vm, printed) = Vm();
            Rect(vm, SKColors.Red, 100, 100, 300, 250);
            PickupOps.PromoteRect(vm.Document, new SKRect(90, 90, 310, 260));
            var fp = vm.Document.FloatingPickup!;
            PickupOps.EnsureLazyErase(vm.Document, fp);
            PickupOps.Translate(fp, 400, 150);
            int cursor = vm.Document.History.Cursor;
            bool dirty = vm.IsDirty;
            vm.PrintCommand.Execute(null);
            var s = Assert.Single(printed);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Px(s, 600, 330));   // на новом месте
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, Px(s, 200, 180)); // старое - пусто
            Assert.Same(fp, vm.Document.FloatingPickup);
            Assert.Equal(cursor, vm.Document.History.Cursor);
            Assert.Equal(dirty, vm.IsDirty);
        });
    }

    [Fact]
    // слои и их прозрачность - на листе так же, как в сохранённом файле (одна сборка)
    public void layers_print_like_the_saved_file()
    {
        WpfRunner.Run(() =>
        {
            var (vm, printed) = Vm();
            Rect(vm, SKColors.Red, 100, 100, 300, 250);
            typeof(MainViewModel).GetMethod("AddLayer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, null);
            Rect(vm, SKColors.Blue, 250, 200, 450, 350);
            vm.Document.ActiveLayer.Opacity = 0.5f;
            vm.PrintCommand.Execute(null);
            using var flat = FileService.Flatten(vm.Document);
            var s = Assert.Single(printed);
            foreach (var (x, y) in new[] { (150, 150), (280, 230), (400, 300), (700, 500) })
            {
                var c = flat.GetPixel(x, y);
                Assert.Equal(new[] { c.Blue, c.Green, c.Red, c.Alpha }, Px(s, x, y));
            }
        });
    }

    [Fact]
    // передумал в окне печати - ничего не происходит
    public void cancelling_the_print_dialog_changes_nothing()
    {
        WpfRunner.Run(() =>
        {
            var (vm, printed) = Vm(accept: false);
            Rect(vm, SKColors.Red, 100, 100, 300, 250);
            int cursor = vm.Document.History.Cursor;
            vm.PrintCommand.Execute(null);
            Assert.Single(printed);
            Assert.Equal(cursor, vm.Document.History.Cursor);
            Assert.Null(vm.StatusHint is { Length: > 0 } h && h.Contains("печат", StringComparison.OrdinalIgnoreCase) ? h : null);
        });
    }

    [Fact]
    // по умолчанию печатает системное окно печати
    public void the_default_printer_is_the_system_dialog()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            Assert.Equal(nameof(PrintService.PrintWithDialog), vm.Printer.Method.Name);
        });
    }

    // ───────── меню и клавиша ─────────

    [Fact]
    // Ctrl+P и пункт «Файл → Печать…» в разметке, как в Electron-версии
    public void ctrl_p_and_the_menu_item_are_wired()
    {
        var xaml = File.ReadAllText(IOPath.Combine(RepoRoot(), "src", "PaintPro.Wpf", "MainWindow.xaml"));
        Assert.Matches(new Regex(@"<KeyBinding Key=""P""\s+Modifiers=""Ctrl"" Command=""\{Binding PrintCommand\}""/>"), xaml);
        Assert.Matches(new Regex(@"<MenuItem Header=""Печать…"" InputGestureText=""Ctrl\+P"" Command=""\{Binding PrintCommand\}""/>"), xaml);
        // буква P без Ctrl - по-прежнему карандаш
        Assert.Matches(new Regex(@"<KeyBinding Key=""P""\s+Command=""\{Binding SelectToolCommand\}"" CommandParameter=""Pencil""/>"), xaml);
        var html = File.ReadAllText(IOPath.Combine(RepoRoot(), "paint-pro-electron", "paint-pro.html"));
        Assert.Contains("<span>Печать…</span><span class=\"kb\">Ctrl P</span>", html);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(IOPath.Combine(dir.FullName, "PaintPro.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root");
    }
}
