using System.Windows;
using System.Windows.Controls;
using PaintPro.Commands;
using PaintPro.Services;
using PaintPro.ViewModels;
using PaintPro.Views;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Потолок масштаба.
///
/// С 1.21.0 он зависел от холста: SKElement растрировал себя во всю поверхность «холст ×
/// масштаб», и фотография 4000x3000 на восьмикратном увеличении просила три гигабайта и
/// роняла приложение. Потолок в 64 млн пикселей поверхности это закрыл, но такой
/// фотографии больше 200% не давал. С 1.30.0 растр размером с окно просмотра, масштаб
/// памяти не просит, и потолок один на все холсты - 800%, как в Electron-версии. Проверки
/// ниже держат обе стороны: масштаб доходит до верха, а растр при этом не растёт.
/// </summary>
public class ZoomCeilingTests
{
    [Theory]
    [InlineData(900, 600)]
    [InlineData(1920, 1080)]
    [InlineData(4000, 3000)]
    [InlineData(12000, 10000)]
    [InlineData(20000, 6000)]
    [InlineData(1, 1)]
    // потолок один на все холсты - верхняя ступень
    public void the_ceiling_is_the_top_step_for_every_canvas(int w, int h)
        => Assert.Equal(GeometryMath.ZoomSteps[^1], ViewGeometry.LargestAllowedZoom(w, h));

    [Theory]
    [InlineData(4000, 3000)]
    [InlineData(8000, 8000)]
    [InlineData(12000, 10000)]
    // фотография доходит до восьмикратного - до 1.30.0 ей давали 200%
    public void a_big_canvas_reaches_the_top_zoom(int w, int h)
    {
        var vm = new MainViewModel();
        vm.Document.History.ExecuteAndPush(new ResizeCanvasCommand(w, h), vm.Document);
        for (int i = 0; i < 12; i++) vm.ZoomInCommand.Execute(null);
        Assert.Equal(8.0, vm.Zoom, 3);
    }

    [Fact]
    // упёрлись в потолок - подсказка про предел масштаба, а не про память
    public void zooming_in_at_the_top_explains_the_limit()
    {
        var vm = new MainViewModel();
        for (int i = 0; i < 12; i++) vm.ZoomInCommand.Execute(null);
        Assert.Equal(8.0, vm.Zoom, 3);
        vm.ZoomInCommand.Execute(null);
        Assert.Equal(8.0, vm.Zoom, 3);
        Assert.Contains("предел масштаба", vm.StatusHint);
        Assert.DoesNotContain("памят", vm.StatusHint);
    }

    [Fact]
    // открыли большую картинку при восьмикратном масштабе - масштаб остаётся
    public void opening_a_big_image_keeps_the_zoom()
    {
        var vm = new MainViewModel();
        for (int i = 0; i < 12; i++) vm.ZoomInCommand.Execute(null);
        Assert.Equal(8.0, vm.Zoom, 3);

        using var big = new SKBitmap(4000, 3000, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(big)) c.Clear(SKColors.White);
        vm.ApplyOpenedBitmap(big);

        Assert.Equal(4000, vm.Document.CanvasWidth);
        Assert.Equal(8.0, vm.Zoom, 3);
    }

    [Theory]
    [InlineData(4000, 3000)]
    [InlineData(12000, 10000)]
    [InlineData(20000, 6000)]
    // на восьмикратном растр по-прежнему размером с окно: масштаб памяти не просит
    public void at_the_top_zoom_the_raster_stays_the_size_of_the_view(int w, int h)
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.Document.History.ExecuteAndPush(new ResizeCanvasCommand(w, h), vm.Document);
            vm.Zoom = ViewGeometry.LargestAllowedZoom(w, h);
            var view = new CanvasView { DataContext = vm };
            view.Measure(new Size(1200, 900));
            view.Arrange(new Rect(0, 0, 1200, 900));
            view.UpdateLayout();
            var raster = (FrameworkElement)view.FindName("Raster")!;
            var scroll = (ScrollViewer)view.FindName("Scroll")!;
            Assert.True(raster.ActualWidth * raster.ActualHeight <= (scroll.ViewportWidth + 4) * (scroll.ViewportHeight + 4),
                $"растр {raster.ActualWidth}x{raster.ActualHeight} при окне {scroll.ViewportWidth}x{scroll.ViewportHeight}");
        });
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

    [Fact]
    // «1:1» даёт один к одному на любом холсте
    public void zoom_reset_gives_one_to_one_on_any_canvas()
    {
        var vm = new MainViewModel();
        vm.Document.History.ExecuteAndPush(new ResizeCanvasCommand(12000, 10000), vm.Document);
        vm.Zoom = 0.1;
        vm.ZoomResetCommand.Execute(null);
        Assert.Equal(1.0, vm.Zoom, 3);
    }
}
