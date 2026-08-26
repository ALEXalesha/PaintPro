using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.ViewModels;
using PaintPro.Views;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Жесты на холсте: захват мыши, его потеря и чужая кнопка.
///
/// Захват уходит сам собой - чужое окно вышло вперёд, Alt+Tab, модальный диалог, - и тогда
/// MouseUp не придёт совсем. Штрих, оставшийся незакрытым, пропадал вместе со своим
/// битмапом размером с холст, а брошенный включённым признак жеста прятал ручки насовсем.
/// Чинилось это по одному случаю за релиз и до 1.21.0 не проверялось ничем.
/// </summary>
public class CanvasGestureTests
{
    private static (CanvasView View, FrameworkElement Skia, Canvas Overlay) Show(MainViewModel vm)
    {
        var view = new CanvasView { DataContext = vm };
        view.Measure(new Size(1200, 900));
        view.Arrange(new Rect(0, 0, 1200, 900));
        return (view, (FrameworkElement)view.FindName("Skia")!, (Canvas)view.FindName("Overlay")!);
    }

    private static void Press(FrameworkElement el, MouseButton button = MouseButton.Left)
        => el.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
        {
            RoutedEvent = Mouse.MouseDownEvent, Source = el,
        });

    private static void Release(FrameworkElement el, MouseButton button = MouseButton.Left)
        => el.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
        {
            RoutedEvent = Mouse.MouseUpEvent, Source = el,
        });

    private static void LoseCapture(FrameworkElement el)
        => el.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = Mouse.LostMouseCaptureEvent, Source = el,
        });

    private static void Leave(FrameworkElement el)
        => el.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = Mouse.MouseLeaveEvent, Source = el,
        });

    private static MainViewModel Painted()
    {
        var vm = new MainViewModel();
        using var c = new SKCanvas(((PixelLayer)vm.Document.Layers[0]).Bitmap);
        c.DrawRect(new SKRect(0, 0, 300, 300), new SKPaint { Color = SKColors.Red });
        return vm;
    }

    [Fact] // нажатие доходит до инструмента
    public void press_reaches_the_tool()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.ActiveTool = ToolKind.Brush;
            vm.PrimaryColor = SKColors.Blue;
            var (_, skia, _) = Show(vm);

            Press(skia);
            Assert.True(vm.ToolContext.IsDrawing, "нажатие не дошло до инструмента");
            Release(skia);
            Assert.False(vm.ToolContext.IsDrawing);
        });
    }

    [Fact]
    // правая кнопка инструмент не запускает
    public void right_button_does_not_draw()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.ActiveTool = ToolKind.Brush;
            var (_, skia, _) = Show(vm);

            Press(skia, MouseButton.Right);
            Assert.False(vm.ToolContext.IsDrawing);
            Assert.Empty(vm.Document.History.Commands);
        });
    }

    [Fact]
    // потеря захвата посреди штриха закрывает жест, а не бросает его
    public void lost_capture_closes_the_stroke()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.ActiveTool = ToolKind.Brush;
            vm.PrimaryColor = SKColors.Blue;
            vm.ToolSize = 20;
            var (_, skia, _) = Show(vm);

            Press(skia);
            LoseCapture(skia);

            Assert.False(vm.ToolContext.IsDrawing);
            Assert.Single(vm.Document.History.Commands);
        });
    }

    [Fact]
    // потеря захвата дважды не даёт двух записей
    public void lost_capture_is_not_counted_twice()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.ActiveTool = ToolKind.Brush;
            vm.PrimaryColor = SKColors.Blue;
            vm.ToolSize = 20;
            var (_, skia, _) = Show(vm);

            Press(skia);
            LoseCapture(skia);
            LoseCapture(skia);

            Assert.Single(vm.Document.History.Commands);
        });
    }

    [Fact]
    // отпускание кнопки не запускает обработчик потери захвата вторым разом
    public void release_does_not_double_close()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.ActiveTool = ToolKind.Brush;
            vm.PrimaryColor = SKColors.Blue;
            vm.ToolSize = 20;
            var (_, skia, _) = Show(vm);

            Press(skia);
            Release(skia);
            LoseCapture(skia);

            Assert.Single(vm.Document.History.Commands);
        });
    }

    [Fact]
    // уход курсора с холста гасит и координаты, и цвет в статусбаре
    public void leaving_the_canvas_clears_the_status()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var (view, skia, _) = Show(vm);
            SKPoint? lastPos = new SKPoint(1, 1);
            SKColor? lastColor = SKColors.Red;
            view.PixelPositionChanged += p => lastPos = p;
            view.PixelColorChanged += c => lastColor = c;

            Leave(skia);

            Assert.Null(lastPos);
            Assert.Null(lastColor);
        });
    }

    [Fact]
    // потеря захвата ручкой снимает признак жеста, иначе ручки прячутся навсегда
    public void handle_drag_survives_lost_capture()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            PickupOps.PromoteRect(vm.Document, new SKRect(50, 50, 250, 250));
            var (_, _, overlay) = Show(vm);

            vm.ToolContext.IsDrawing = true;   // как будто тянут ручку
            typeof(CanvasView)
                .GetField("_draggingHandle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(overlay.Parent is null ? null : FindView(overlay), ResizeHandle.SE);

            LoseCapture(overlay);

            Assert.False(vm.ToolContext.IsDrawing);
        });
    }

    private static CanvasView FindView(DependencyObject start)
    {
        var cur = start;
        while (cur is not null and not CanvasView) cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        return (CanvasView)cur!;
    }

    [Fact]
    // смена инструмента посреди штриха не оставляет жест открытым
    public void tool_switch_mid_stroke_closes_the_gesture()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.ActiveTool = ToolKind.Brush;
            var (_, skia, _) = Show(vm);

            Press(skia);
            vm.ActiveTool = ToolKind.Select;

            Assert.False(vm.ToolContext.IsDrawing);
        });
    }

    [Fact]
    // жест «Рукой» не пишет в историю
    public void hand_tool_records_nothing()
    {
        WpfRunner.Run(() =>
        {
            var vm = Painted();
            vm.ActiveTool = ToolKind.Hand;
            var (_, skia, _) = Show(vm);

            Press(skia);
            Release(skia);

            Assert.Empty(vm.Document.History.Commands);
            Assert.False(vm.ToolContext.IsDrawing);
        });
    }
}
