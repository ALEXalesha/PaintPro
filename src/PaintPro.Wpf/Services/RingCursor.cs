using System.IO;
using System.Windows.Input;
using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// Курсор с кружком размера кисти и крестиком (1.33.0).
///
/// Кружок-элемент поверх холста отстаёт от указателя на кадр-другой: WPF рисует его в
/// следующем кадре, а указатель двигает система сразу, и кружок заметно тянулся за
/// крестиком. Курсор-картинку система рисует вместе с указателем, без задержки. Поэтому
/// кружок, который помещается в курсор, - в курсоре; больше - элементом, а системный
/// указатель прячется, и крестик рисуется тем же элементом (CanvasView). Граница та же, что в
/// Electron-версии: там её диктует Chromium, у которого курсор не больше 128 точек.
/// </summary>
public static class RingCursor
{
    /// <summary>Наибольший диаметр кружка в курсоре, в пикселях экрана.</summary>
    public const double MaxDiameter = 120;

    /// <summary>Поле вокруг кружка под белую обводку.</summary>
    public const int Pad = 3;

    /// <summary>Половина длины штриха крестика.</summary>
    public const int CrossArm = 5;

    public static bool Fits(double diameterPixels) => diameterPixels <= MaxDiameter;

    /// <summary>Сторона картинки курсора для кружка такого диаметра.</summary>
    public static int SideFor(double diameterPixels)
        => (int)Math.Ceiling(Math.Max(1, diameterPixels) + Pad * 2) | 1; // нечётная: центр - ровно пиксель

    /// <summary>Картинка курсора: белая обводка и чёрная линия кружка, крестик в центре.</summary>
    public static SKBitmap Render(double diameterPixels)
    {
        int side = SideFor(diameterPixels);
        var bmp = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
        bmp.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(bmp);
        float c = side / 2f;
        float r = (float)Math.Max(0.5, diameterPixels / 2);
        using var white = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f, Color = SKColors.White.WithAlpha(230) };
        using var black = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = SKColors.Black };
        canvas.DrawCircle(c, c, r, white);
        canvas.DrawCircle(c, c, r, black);
        using var crossWhite = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 3f, Color = SKColors.White.WithAlpha(230) };
        using var crossBlack = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = SKColors.Black };
        foreach (var p in new[] { crossWhite, crossBlack })
        {
            canvas.DrawLine(c - CrossArm, c, c + CrossArm, c, p);
            canvas.DrawLine(c, c - CrossArm, c, c + CrossArm, p);
        }
        canvas.Flush();
        return bmp;
    }

    /// <summary>
    /// Файл курсора (.cur) из картинки: заголовок, 32-битный DIB снизу вверх и пустая
    /// маска AND - прозрачность берётся из альфы.
    /// </summary>
    public static byte[] BuildCur(double diameterPixels)
    {
        using var bmp = Render(diameterPixels);
        int s = bmp.Width;
        int maskStride = (s + 31) / 32 * 4;
        int dib = 40 + s * s * 4 + maskStride * s;
        using var ms = new MemoryStream(22 + dib);
        using var w = new BinaryWriter(ms);
        // ICONDIR
        w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)1);
        // ICONDIRENTRY: 0 в размере значит 256
        w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)(s / 2)); w.Write((ushort)(s / 2)); // горячая точка - центр
        w.Write(dib); w.Write(22);
        // BITMAPINFOHEADER: высота удвоена - картинка плюс маска
        w.Write(40); w.Write(s); w.Write(s * 2); w.Write((ushort)1); w.Write((ushort)32);
        w.Write(0); w.Write(s * s * 4); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = s - 1; y >= 0; y--)
            for (int x = 0; x < s; x++)
            {
                var px = bmp.GetPixel(x, y);
                w.Write(px.Blue); w.Write(px.Green); w.Write(px.Red); w.Write(px.Alpha);
            }
        w.Write(new byte[maskStride * s]);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Курсор WPF. В пикселях экрана как есть: масштаб экрана уже учтён в диаметре.</summary>
    public static Cursor Create(double diameterPixels)
        => new(new MemoryStream(BuildCur(diameterPixels)), scaleWithDpi: false);
}
