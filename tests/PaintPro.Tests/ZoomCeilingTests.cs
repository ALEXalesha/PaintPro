using PaintPro.Commands;
using PaintPro.Services;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Потолок на «холст × масштаб».
///
/// SKElement растрирует себя целиком, в размер своего элемента, а размер этот - холст,
/// умноженный на масштаб; виртуализации по видимой области там нет. Потолок стоял только у
/// самого документа - сторона до 20000 и до 120 млн пикселей всего, - а произведение не
/// проверял никто. Обычная фотография 4000x3000 на восьмикратном увеличении просила без
/// малого три гигабайта, самый большой разрешённый холст - двадцать девять, и приложение
/// падало нехваткой памяти прямо в отрисовке. Дотянуться до этого можно было четырьмя
/// нажатиями Ctrl+= .
/// </summary>
public class ZoomCeilingTests
{
    /// <summary>
    /// SKElement растрирует себя целиком, в размер своего элемента: поверхность в
    /// canvas × zoom пикселей означает WriteableBitmap на столько же пикселей по 4 байта.
    /// </summary>
    private static double SurfaceMegabytes(int canvasW, int canvasH, double zoom)
    {
        var (w, h) = ViewGeometry.SurfaceSize(canvasW, canvasH, zoom);
        return w * h * 4 / 1024.0 / 1024.0;
    }

    [Fact]
    // холст по умолчанию на максимальном зуме - разумная память
    public void default_canvas_at_max_zoom_is_fine()
    {
        Assert.True(SurfaceMegabytes(900, 600, 8) < 200, $"{SurfaceMegabytes(900, 600, 8):F0} МБ");
    }

    [Fact]
    // обычная фотография на максимальном зуме
    public void a_photo_at_max_zoom_is_bounded()
    {
        double zoom = ViewGeometry.LargestAllowedZoom(4000, 3000);
        double mb = SurfaceMegabytes(4000, 3000, zoom);
        Assert.True(mb < 512, $"фотография 4000x3000 на потолочном зуме {zoom} просит {mb:F0} МБ");
    }

    [Fact]
    // самый большой разрешённый холст на максимальном зуме
    public void the_largest_allowed_canvas_is_bounded()
    {
        // Потолок документа: сторона до 20000, всего до 120 млн пикселей.
        double zoom = ViewGeometry.LargestAllowedZoom(12000, 10000);
        double mb = SurfaceMegabytes(12000, 10000, zoom);
        Assert.True(mb < 512, $"холст 12000x10000 на потолочном зуме {zoom} просит {mb:F0} МБ");
    }

    [Fact]
    // зум ограничен размером холста
    public void zoom_is_capped_by_the_canvas()
    {
        var vm = new MainViewModel();
        vm.Document.History.ExecuteAndPush(new ResizeCanvasCommand(4000, 3000), vm.Document);
        for (int i = 0; i < 12; i++) vm.ZoomInCommand.Execute(null);
        double mb = SurfaceMegabytes(4000, 3000, vm.Zoom);
        Assert.True(mb < 512, $"после двенадцати Ctrl+= поверхность просит {mb:F0} МБ (зум {vm.Zoom})");
    }

    [Fact]
    // открытие большой картинки при уже задранном зуме сбрасывает его
    public void opening_a_big_image_reins_in_the_zoom()
    {
        var vm = new MainViewModel();
        for (int i = 0; i < 12; i++) vm.ZoomInCommand.Execute(null);
        Assert.Equal(8.0, vm.Zoom, 3);

        using var big = new SKBitmap(4000, 3000, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(big)) c.Clear(SKColors.White);
        vm.ApplyOpenedBitmap(big);

        double mb = SurfaceMegabytes(vm.Document.CanvasWidth, vm.Document.CanvasHeight, vm.Zoom);
        Assert.True(mb < 512, $"после открытия поверхность просит {mb:F0} МБ (зум {vm.Zoom})");
    }

    [Fact]
    // на маленьком холсте зум по-прежнему доходит до восьмикратного
    public void small_canvas_still_reaches_max_zoom()
    {
        var vm = new MainViewModel();
        for (int i = 0; i < 12; i++) vm.ZoomInCommand.Execute(null);
        Assert.Equal(8.0, vm.Zoom, 3);
    }

    [Fact]
    // уменьшать можно всегда
    public void zoom_out_always_works()
    {
        var vm = new MainViewModel();
        vm.Document.History.ExecuteAndPush(new ResizeCanvasCommand(8000, 8000), vm.Document);
        for (int i = 0; i < 12; i++) vm.ZoomOutCommand.Execute(null);
        Assert.Equal(0.1, vm.Zoom, 3);
    }
}
