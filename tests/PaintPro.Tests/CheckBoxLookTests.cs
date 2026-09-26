using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Флажок на стекле («Заливать фигуры», галочки ленты истории) - в натуральную величину.
///
/// Алексей 26.09.2026: «исправь галочки в Paint Pro». Два недочёта, оба видны только в 1x:
///   1. включённый флажок сжимался на 2 px: рамка в 1 px становилась прозрачной, а WPF-овский
///      Border красит фон только ВНУТРИ рамки - вместо квадрата 16 оставался 14;
///   2. галочка сидела на пиксель правее и ниже середины: её путь считался от внутреннего
///      края рамки. В Electron-версии флажок родной, Chromium (accent-color): сплошной
///      квадрат и галочка посередине.
/// </summary>
public class CheckBoxLookTests
{
    private static readonly Color Page = Color.FromRgb(0x1C, 0x21, 0x28);

    // Кадр 16x16 флажка со стилем из GlassStyles.xaml, с округлением разметки, как в окне.
    private static byte[] Render(bool isChecked) => WpfRunner.Invoke(() =>
    {
        var box = new CheckBox { IsChecked = isChecked };
        var host = new Grid { Background = new SolidColorBrush(Page), Width = 16, Height = 16, UseLayoutRounding = true };
        host.Children.Add(box);
        host.Measure(new Size(16, 16));
        host.Arrange(new Rect(0, 0, 16, 16));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var px = new byte[16 * 16 * 4];
        new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0).CopyPixels(px, 16 * 4, 0);
        return px;
    });

    private static Color At(byte[] px, int x, int y)
    {
        var i = (y * 16 + x) * 4;
        return Color.FromRgb(px[i + 2], px[i + 1], px[i]);
    }

    private static bool IsPage(Color c) =>
        Math.Abs(c.R - Page.R) + Math.Abs(c.G - Page.G) + Math.Abs(c.B - Page.B) < 12;

    [Fact]
    public void Включённый_флажок_целый_квадрат_16()
    {
        var px = Render(true);
        // Середины четырёх сторон - уже квадрат, а не страница (углы скруглены, их не спрашиваем).
        foreach (var (x, y) in new[] { (0, 8), (15, 8), (8, 0), (8, 15) })
            Assert.False(IsPage(At(px, x, y)), $"точка ({x}; {y}) включённого флажка - цвет страницы: квадрат сжался");
    }

    [Fact]
    public void Галочка_посередине_квадрата()
    {
        var px = Render(true);
        // Центр тяжести белого: галочка - единственное белое на цветном квадрате.
        double sx = 0, sy = 0, sw = 0;
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                var c = At(px, x, y);
                var w = Math.Max(0, Math.Min(c.R, Math.Min(c.G, c.B)) - 200);
                sx += w * x; sy += w * y; sw += w;
            }
        Assert.True(sw > 0, "галочки нет");
        var (cx, cy) = (sx / sw, sy / sw);
        Assert.True(Math.Abs(cx - 7.5) < 0.5 && Math.Abs(cy - 7.5) < 0.5,
            $"центр галочки ({cx:F2}; {cy:F2}), середина квадрата (7.5; 7.5)");
    }

    [Fact]
    public void Выключенный_флажок_с_рамкой_и_без_галочки()
    {
        var px = Render(false);
        Assert.False(IsPage(At(px, 0, 8)), "у выключенного флажка нет рамки");
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
                Assert.True(Math.Min(At(px, x, y).R, Math.Min(At(px, x, y).G, At(px, x, y).B)) < 200, $"белое в ({x}; {y}) у выключенного флажка");
    }
}
