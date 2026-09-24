using System.Globalization;
using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// Выбор цвета (1.28.0): переводы между RGB, HSV и HEX для окна выбора цвета. Окно
/// показывает квадрат (насыщенность по горизонтали, яркость по вертикали) и полосу тона;
/// здесь только арифметика, её проверяют тесты. Та же арифметика - в Electron-версии
/// (paint-pro.html, colorMath), чтобы две версии давали один цвет при одном положении.
/// </summary>
public static class ColorMath
{
    /// <summary>Тон 0..360, насыщенность и яркость 0..1.</summary>
    public readonly record struct Hsv(double H, double S, double V);

    public static Hsv ToHsv(SKColor c)
    {
        double r = c.Red / 255.0, g = c.Green / 255.0, b = c.Blue / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return new Hsv(h, max == 0 ? 0 : d / max, max);
    }

    public static SKColor FromHsv(Hsv hsv, byte alpha = 255)
    {
        var h = ((hsv.H % 360) + 360) % 360;
        var s = Math.Clamp(hsv.S, 0, 1);
        var v = Math.Clamp(hsv.V, 0, 1);
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new SKColor(Byte(r + m), Byte(g + m), Byte(b + m), alpha);
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    /// <summary>«#RRGGBB» заглавными.</summary>
    public static string ToHex(SKColor c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    /// <summary>
    /// Разбор того, что человек ввёл в поле: «#5B8DEF», «5b8def», «#abc», с пробелами по
    /// краям. Всё остальное - null (поле подсвечивается, цвет не меняется).
    /// </summary>
    public static SKColor? ParseHex(string? text)
    {
        var t = text?.Trim().TrimStart('#');
        if (t is null || (t.Length != 6 && t.Length != 3) || !t.All(Uri.IsHexDigit)) return null;
        if (t.Length == 3) t = string.Concat(t.Select(ch => new string(ch, 2)));
        var v = uint.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
