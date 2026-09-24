using System.Globalization;

namespace PaintPro.Services;

/// <summary>
/// Размер и место окна между запусками (1.28.0). То же правило, что у калькуляторов
/// (CalcPro WindowPlacement, window-state.js): что бы ни лежало в файле - окно на отключённом мониторе,
/// размер больше экрана или меньше минимума, мусор, - окно открывается там, где его
/// видно и можно взять за заголовок. Здесь только правило и формат файла, без WPF.
/// </summary>
public static class WindowPlacement
{
    /// <summary>Прямоугольник в пикселях экрана.</summary>
    public readonly record struct Area(double X, double Y, double Width, double Height)
    {
        public bool IsSane =>
            double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
    }

    /// <summary>Что сохранить и что восстановить. Left/Top = null - поставить по центру.</summary>
    public sealed record Placement(double? Left, double? Top, double Width, double Height, bool Maximized);

    /// <summary>Полоса заголовка, которая должна остаться на экране (высота своего заголовка окна).</summary>
    public const double GripHeight = 36;
    public const double GripWidth = 80;

    /// <summary>
    /// Где и какого размера открыть окно. <paramref name="screens"/> - рабочие области
    /// экранов, основной первым. Без сохранённого - размер по умолчанию по центру.
    /// </summary>
    public static Placement Restore(Placement? saved, IReadOnlyList<Area> screens, double width, double height, double minWidth, double minHeight)
    {
        var byDefault = new Placement(null, null, width, height, false);
        if (saved is null || !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height)) return byDefault;
        var areas = screens.Where(a => a.IsSane).ToList();

        // Самый большой экран ограничивает размер сверху.
        var maxW = Math.Max(width, areas.Count == 0 ? width : areas.Max(a => a.Width));
        var maxH = Math.Max(height, areas.Count == 0 ? height : areas.Max(a => a.Height));
        var w = Math.Round(Math.Min(Math.Max(saved.Width, minWidth), Math.Max(maxW, minWidth)));
        var h = Math.Round(Math.Min(Math.Max(saved.Height, minHeight), Math.Max(maxH, minHeight)));

        if (saved.Left is not { } left || saved.Top is not { } top || !double.IsFinite(left) || !double.IsFinite(top))
            return new Placement(null, null, w, h, saved.Maximized);
        var x = Math.Round(left);
        var y = Math.Round(top);

        // Экран, на котором больше всего полосы заголовка; не видна ни на одном - по центру.
        Area? best = null;
        double bestArea = 0;
        foreach (var a in areas)
        {
            var ow = Math.Min(x + w, a.X + a.Width) - Math.Max(x, a.X);
            var oh = Math.Min(y + GripHeight, a.Y + a.Height) - Math.Max(y, a.Y);
            if (ow > 0 && oh > 0 && ow * oh > bestArea) { best = a; bestArea = ow * oh; }
        }
        if (best is not { } s || bestArea < GripWidth * GripHeight / 2)
            return new Placement(null, null, w, h, saved.Maximized);

        // Съехавшее окно придвигается к краю своего экрана; если оно выше экрана - верхом,
        // чтобы заголовок остался виден (та же ошибка была найдена в window-state.js).
        w = Math.Min(w, Math.Max(s.Width, minWidth));
        h = Math.Min(h, Math.Max(s.Height, minHeight));
        var nx = Math.Max(s.X, Math.Min(x, s.X + s.Width - w));
        var ny = Math.Max(s.Y, Math.Min(y, s.Y + s.Height - h));
        return new Placement(nx, ny, w, h, saved.Maximized);
    }

    /// <summary>Строка для файла: «left;top;width;height;maximized», числа в инвариантной культуре.</summary>
    public static string Format(Placement p) => string.Join(";",
        Num(p.Left), Num(p.Top), Num(p.Width), Num(p.Height), p.Maximized ? "max" : "normal");

    /// <summary>Разбор строки из файла; мусор, обрезанный или пустой файл - null.</summary>
    public static Placement? Parse(string? text)
    {
        var parts = text?.Trim().Split(';');
        if (parts is not { Length: 5 }) return null;
        double? Opt(string s) => s == "" ? null : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : double.NaN;
        var left = Opt(parts[0]);
        var top = Opt(parts[1]);
        var width = Opt(parts[2]);
        var height = Opt(parts[3]);
        if (left is double.NaN || top is double.NaN || width is not { } w || height is not { } h || double.IsNaN(w) || double.IsNaN(h)) return null;
        if (parts[4] is not ("max" or "normal")) return null;
        return new Placement(left, top, w, h, parts[4] == "max");
    }

    private static string Num(double? v) => v is { } d ? d.ToString("R", CultureInfo.InvariantCulture) : "";
}
