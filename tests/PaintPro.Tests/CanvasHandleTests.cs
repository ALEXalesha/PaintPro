using System.Linq;
using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Растягивание холста за любую сторону и любой угол.
///
/// До 1.25.0 холст здесь менял размер только через диалог с числами. Ручки по краям
/// делятся на две породы, и в этом вся суть проверок: правая и нижняя двигают только
/// край - рисунок стоит на месте; левая и верхняя двигают НАЧАЛО холста, и рисунок
/// обязан поехать вместе с ним. Иначе «потянул влево» на экране выглядит как «рисунок
/// прыгнул вправо» - жест сделал не то, что показывал.
/// </summary>
public class CanvasHandleTests
{
    private const int W = 900;
    private const int H = 600;

    private static (int W, int H, int OffX, int OffY) Resize(
        ResizeHandle edge, int dm, int dn, bool ratio = false)
        => ViewGeometry.CanvasResize(edge, W, H, dm, dn, ratio);

    [Fact]
    public void Правая_и_нижняя_двигают_край_рисунок_стоит_на_месте()
    {
        Assert.Equal((1000, 600, 0, 0), Resize(ResizeHandle.E, 100, 0));
        Assert.Equal((900, 700, 0, 0), Resize(ResizeHandle.S, 0, 100));
        Assert.Equal((1000, 700, 0, 0), Resize(ResizeHandle.SE, 100, 100));
    }

    [Fact]
    public void Левая_и_верхняя_двигают_начало_холста()
    {
        // Тянем влево - мышь идёт в минус, холст растёт, начало уезжает на те же 100.
        Assert.Equal((1000, 600, 100, 0), Resize(ResizeHandle.W, -100, 0));
        Assert.Equal((900, 700, 0, 100), Resize(ResizeHandle.N, 0, -100));
        Assert.Equal((1000, 700, 100, 100), Resize(ResizeHandle.NW, -100, -100));
    }

    [Fact]
    public void Сжатие_слева_срезает_рисунок_а_не_отодвигает_его()
    {
        var (w, _, offX, _) = Resize(ResizeHandle.W, 100, 0);
        Assert.Equal(800, w);
        Assert.Equal(-100, offX);

        var (_, h, _, offY) = Resize(ResizeHandle.N, 0, 100);
        Assert.Equal(500, h);
        Assert.Equal(-100, offY);
    }

    [Fact]
    public void Смещение_считается_после_ограничений_а_не_до_них()
    {
        // Иначе на упоре рисунок уехал бы дальше, чем выросла бумага, и часть его
        // оказалась бы за краем холста - потерянной без единого слова.
        var (w, _, offX, _) = Resize(ResizeHandle.W, -1_000_000, 0);
        Assert.Equal(w - W, offX);
        Assert.True(ResizeCanvasCommand.IsAllowed(w, H), "предложен размер, который не применится");
    }

    [Fact]
    public void Холст_не_сжимается_в_точку_ни_одной_ручкой()
    {
        foreach (var edge in Enum.GetValues<ResizeHandle>())
        {
            var (w, h, _, _) = Resize(edge, 100_000, 100_000);
            Assert.True(w >= ViewGeometry.MinCanvasSide, edge.ToString());
            Assert.True(h >= ViewGeometry.MinCanvasSide, edge.ToString());
        }
    }

    [Fact]
    public void Ни_одна_ручка_не_предлагает_запрещённый_размер()
    {
        // Ручка не диалог: объяснять промах ей нечем, значит промахнуться она не должна.
        int[] pushes = { -1_000_000, -5000, -1, 0, 1, 5000, 1_000_000 };
        foreach (var edge in Enum.GetValues<ResizeHandle>())
            foreach (var dm in pushes)
                foreach (var dn in pushes)
                {
                    var (w, h, _, _) = Resize(edge, dm, dn);
                    Assert.True(ResizeCanvasCommand.IsAllowed(w, h), $"{edge} {dm}x{dn} -> {w}x{h}");
                }
    }

    [Fact]
    public void Shift_на_углу_держит_пропорции_на_стороне_не_мешает()
    {
        var (cw, ch, _, _) = Resize(ResizeHandle.SE, 300, 0, ratio: true);
        Assert.True(Math.Abs((double)cw / ch - (double)W / H) < 0.01);

        var side = Resize(ResizeHandle.E, 300, 0, ratio: true);
        Assert.Equal((1200, 600, 0, 0), side);
    }

    [Fact]
    public void Пределы_ручки_и_команды_смены_размера_совпадают()
    {
        // Разойдись они - ручка предлагала бы размер, который потом не применится, и
        // жест молча не делал бы ничего.
        var (w, h) = ViewGeometry.ClampCanvasSize(int.MaxValue, int.MaxValue);
        Assert.True(ResizeCanvasCommand.IsAllowed(w, h));

        var (sw, sh) = ViewGeometry.ClampCanvasSize(0, 0);
        Assert.Equal(ViewGeometry.MinCanvasSide, sw);
        Assert.Equal(ViewGeometry.MinCanvasSide, sh);
    }

    [Fact]
    public void Ручки_стоят_за_краем_холста_и_помещаются_в_поле()
    {
        // Половина поперёк края накрывала бы крайние пиксели рисунка и глотала бы клики,
        // адресованные инструменту. Места хватает - вокруг поверхности лежит поле.
        const double surfaceW = 900, surfaceH = 600, t = 8, len = 40, gap = 6;
        foreach (var edge in Enum.GetValues<ResizeHandle>())
        {
            var (left, top, w, h) = ViewGeometry.CanvasHandleBox(edge, surfaceW, surfaceH, t, len, gap);

            bool outside = left + w <= 0 || left >= surfaceW || top + h <= 0 || top >= surfaceH;
            Assert.True(outside, $"{edge} лежит поверх холста");

            Assert.True(left >= -ViewGeometry.CanvasMargin, $"{edge} уехала за поле слева");
            Assert.True(top >= -ViewGeometry.CanvasMargin, $"{edge} уехала за поле сверху");
            Assert.True(left + w <= surfaceW + ViewGeometry.CanvasMargin, $"{edge} уехала за поле справа");
            Assert.True(top + h <= surfaceH + ViewGeometry.CanvasMargin, $"{edge} уехала за поле снизу");
        }
    }

    [Fact]
    public void У_каждой_ручки_своё_место()
    {
        var boxes = Enum.GetValues<ResizeHandle>()
            .Select(e => ViewGeometry.CanvasHandleBox(e, 900, 600, 8, 40, 6))
            .ToArray();
        Assert.Equal(boxes.Length, boxes.Distinct().Count());
    }

    // ───────── Применение: документ, слои, история ─────────

    private static MainViewModel Dotted(int x, int y)
    {
        var vm = new MainViewModel();
        var bmp = ((PixelLayer)vm.Document.Layers[0]).Bitmap;
        using var c = new SKCanvas(bmp);
        c.DrawRect(new SKRect(x - 3, y - 3, x + 3, y + 3), new SKPaint { Color = SKColors.Red });
        return vm;
    }

    private static bool IsRed(MainViewModel vm, int layer, int x, int y)
    {
        var p = ((PixelLayer)vm.Document.Layers[layer]).Bitmap.GetPixel(x, y);
        return p.Red > 200 && p.Green < 80 && p.Blue < 80;
    }

    [Fact]
    public void Растягивание_влево_уводит_рисунок_вправо_ровно_на_прирост()
    {
        WpfRunner.Run(() =>
        {
            var vm = Dotted(20, 300);
            Assert.True(IsRed(vm, 0, 20, 300), "пятно не легло");

            Assert.True(vm.ResizeCanvasTo(1000, 600, 100, 0));

            Assert.Equal(1000, vm.Document.CanvasWidth);
            Assert.True(IsRed(vm, 0, 120, 300), "пятно не поехало вместе с краем");
            Assert.False(IsRed(vm, 0, 20, 300), "пятно осталось и на прежнем месте");
        });
    }

    [Fact]
    public void Новое_место_на_бумаге_белое_а_на_верхнем_слое_прозрачное()
    {
        // Белая заплатка на верхнем слое закрыла бы бумагу вместо того, чтобы показать её.
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.Document.Layers.Add(new PixelLayer(vm.Document.CanvasWidth, vm.Document.CanvasHeight,
                                                  SKColors.Transparent));

            Assert.True(vm.ResizeCanvasTo(1000, 600, 100, 0));

            Assert.Equal(SKColors.White, ((PixelLayer)vm.Document.Layers[0]).Bitmap.GetPixel(10, 300));
            Assert.Equal((byte)0, ((PixelLayer)vm.Document.Layers[1]).Bitmap.GetPixel(10, 300).Alpha);
        });
    }

    [Fact]
    public void Смещение_одно_и_то_же_для_всех_слоёв()
    {
        // Разъедься они хоть на пиксель - рисунок расслоится, и заметить это можно будет
        // только глазами.
        WpfRunner.Run(() =>
        {
            var vm = Dotted(20, 300);
            var upper = new PixelLayer(vm.Document.CanvasWidth, vm.Document.CanvasHeight, SKColors.Transparent);
            using (var c = new SKCanvas(upper.Bitmap))
                c.DrawRect(new SKRect(17, 297, 23, 303), new SKPaint { Color = SKColors.Red });
            vm.Document.Layers.Add(upper);

            Assert.True(vm.ResizeCanvasTo(900, 700, 0, 100));

            Assert.True(IsRed(vm, 0, 20, 400), "нижний слой не поехал");
            Assert.True(IsRed(vm, 1, 20, 400), "верхний слой не поехал вместе с нижним");
        });
    }

    [Fact]
    public void Растягивание_пишет_одну_запись_и_отменяется_целиком()
    {
        WpfRunner.Run(() =>
        {
            var vm = Dotted(20, 300);
            int before = vm.Document.History.Commands.Count;

            Assert.True(vm.ResizeCanvasTo(1000, 600, 100, 0));
            Assert.Equal(before + 1, vm.Document.History.Commands.Count);

            vm.Document.History.Undo(vm.Document);
            Assert.Equal(900, vm.Document.CanvasWidth);
            Assert.Equal(600, vm.Document.CanvasHeight);
            Assert.True(IsRed(vm, 0, 20, 300), "отмена не вернула рисунок на место");
        });
    }

    [Fact]
    public void Отмена_возвращает_срезанное_сжатием()
    {
        // Сжатие слева срезает пиксели. Отмена обязана вернуть именно их, а не пустоту.
        WpfRunner.Run(() =>
        {
            var vm = Dotted(20, 300);

            Assert.True(vm.ResizeCanvasTo(800, 600, -100, 0));
            Assert.Equal(800, vm.Document.CanvasWidth);

            vm.Document.History.Undo(vm.Document);
            Assert.Equal(900, vm.Document.CanvasWidth);
            Assert.True(IsRed(vm, 0, 20, 300), "срезанное не вернулось");
        });
    }

    [Fact]
    public void Тот_же_размер_не_правка()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            int before = vm.Document.History.Commands.Count;
            Assert.False(vm.ResizeCanvasTo(vm.Document.CanvasWidth, vm.Document.CanvasHeight, 0, 0));
            Assert.Equal(before, vm.Document.History.Commands.Count);
        });
    }

    [Fact]
    public void Запрещённый_размер_не_применяется()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            int before = vm.Document.History.Commands.Count;
            Assert.False(vm.ResizeCanvasTo(ResizeCanvasCommand.MaxDimension + 1, 600, 0, 0));
            Assert.Equal(900, vm.Document.CanvasWidth);
            Assert.Equal(before, vm.Document.History.Commands.Count);
        });
    }
}
