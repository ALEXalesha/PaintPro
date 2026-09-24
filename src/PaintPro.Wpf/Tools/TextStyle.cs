using System.Globalization;

namespace PaintPro.Tools;

/// <summary>
/// Оформление надписи (1.28.0): размер, шрифт, жирный, курсив, подчёркнутый. Границы
/// размера и значение по умолчанию - как у Electron-версии (TEXT_SIZE_MIN/MAX, 24).
/// </summary>
public sealed record TextStyle(float Size, string Family, bool Bold, bool Italic, bool Underline)
{
    public const float MinSize = 8, MaxSize = 200;

    public static readonly TextStyle Default = new(24, "Segoe UI", false, false, false);

    /// <summary>Разбор поля «размер»: не число - минимум, за краями - к краю (как clampTextSize).</summary>
    public static float ClampSize(string? raw)
    {
        if (!int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < MinSize) return MinSize;
        return Math.Min(v, MaxSize);
    }
}
