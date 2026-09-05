using PaintPro.Models;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Прямоугольный треугольник.
///
/// От остальных фигур он отличается тем, что габарит жеста для него не нормализуется:
/// прямой угол ставится ПО НАПРАВЛЕНИЮ движения мыши. Катеты выходят из точки старта,
/// гипотенуза ложится ровно на линию, которую ведёт рука, - поэтому один и тот же
/// прямоугольник даёт четыре разных треугольника, по одному на каждое направление.
/// Те же правила проверены в Electron-версии (tests/right-triangle.spec.js).
/// </summary>
public class RightTriangleTests
{
    private static (Document doc, PixelLayer layer, ToolContext ctx) Fresh(int n = 60)
    {
        var doc = new Document(n, n);
        var layer = (PixelLayer)doc.ActiveLayer;
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Black, ToolSize = 3f, Opacity = 1f };
        return (doc, layer, ctx);
    }

    private static void Gesture(ShapeTool tool, ToolContext ctx, SKPoint a, SKPoint b)
    {
        tool.OnPointerDown(a, ctx);
        tool.OnPointerMove(b, ctx);
        tool.OnPointerUp(b, ctx);
    }

    [Fact]
    public void Катеты_и_гипотенуза_ложатся_по_жесту()
    {
        var (_, layer, ctx) = Fresh();
        Gesture(new RightTriangleShapeTool(), ctx, new SKPoint(15, 15), new SKPoint(45, 45));
        Assert.NotEqual(SKColors.White, layer.Bitmap.GetPixel(15, 30)); // катет по x старта
        Assert.NotEqual(SKColors.White, layer.Bitmap.GetPixel(30, 45)); // катет по y конца
        Assert.NotEqual(SKColors.White, layer.Bitmap.GetPixel(30, 30)); // гипотенуза
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(43, 17));    // снаружи
    }

    [Fact]
    public void Обратное_направление_переворачивает_прямой_угол()
    {
        var (_, layer, ctx) = Fresh();
        Gesture(new RightTriangleShapeTool(), ctx, new SKPoint(15, 45), new SKPoint(45, 15));
        Assert.NotEqual(SKColors.White, layer.Bitmap.GetPixel(15, 30));
        Assert.NotEqual(SKColors.White, layer.Bitmap.GetPixel(30, 15));
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(30, 43));
    }

    [Fact]
    public void Фигура_не_вылезает_за_прямоугольник_жеста()
    {
        var (_, layer, ctx) = Fresh();
        Gesture(new RightTriangleShapeTool(), ctx, new SKPoint(20, 20), new SKPoint(40, 40));
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(5, 5));
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(55, 55));
    }

    [Fact]
    public void Заливка_закрашивает_внутренность_а_не_весь_габарит()
    {
        var (_, layer, ctx) = Fresh();
        Gesture(new RightTriangleShapeTool { Fill = true }, ctx, new SKPoint(10, 10), new SKPoint(50, 50));
        Assert.NotEqual(SKColors.White, layer.Bitmap.GetPixel(15, 45)); // внутри
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(45, 15));    // снаружи
    }

    [Fact]
    public void Прозрачность_в_ноль_не_кладёт_фигуру()
    {
        var (doc, layer, ctx) = Fresh();
        ctx.Opacity = 0f;
        Gesture(new RightTriangleShapeTool(), ctx, new SKPoint(15, 15), new SKPoint(45, 45));
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(15, 30));
        Assert.False(doc.History.CanUndo);
    }

    [Fact]
    public void Отмена_убирает_фигуру_целиком()
    {
        var (doc, layer, ctx) = Fresh();
        Gesture(new RightTriangleShapeTool(), ctx, new SKPoint(15, 15), new SKPoint(45, 45));
        Assert.True(doc.History.CanUndo);
        doc.History.Undo(doc);
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(15, 30));
    }

    [Fact]
    public void Инструмент_числится_фигурой_и_имеет_размер()
    {
        Assert.True(ToolKind.RightTriangle.IsShapeTool());
        Assert.True(ToolKind.RightTriangle.IsDrawingTool());
        Assert.True(MainViewModel.HasToolSize(ToolKind.RightTriangle));
    }

    [Fact]
    public void У_каждого_инструмента_из_перечисления_есть_реализация()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            foreach (var k in Enum.GetValues<ToolKind>())
            {
                vm.ActiveTool = k;
                Assert.NotNull(vm.ActiveToolInstance);
            }
        });
    }

    [Fact]
    public void Инструмент_достаётся_по_имени_с_кнопки()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.SelectToolCommand.Execute("RightTriangle");
            Assert.Equal(ToolKind.RightTriangle, vm.ActiveTool);
        });
    }

    [Fact]
    public void Вырожденный_жест_ничего_не_пачкает()
    {
        var (_, layer, ctx) = Fresh();
        Gesture(new RightTriangleShapeTool(), ctx, new SKPoint(30, 30), new SKPoint(30, 30));
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(10, 10));
    }
}
