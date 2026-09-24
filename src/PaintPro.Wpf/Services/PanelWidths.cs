using System.Globalization;
using System.IO;

namespace PaintPro.Services;

/// <summary>
/// Ширина боковых панелей (1.28.0): обе можно сузить или расширить, потянув за внутренний
/// край, и ширина запоминается в %APPDATA%\PaintPro\panels.txt. Правило то же, что в
/// Electron-версии (paint-pro.html, PANELS и fitPanels).
///
/// Пределы подобраны по содержимому, чтобы ничего не налезало: слева инструменты встают
/// в один, два или три столбца (кнопка 60 px; минимум 88 - это один столбец вместе с
/// полосой прокрутки, при 78 кнопки вылезали из-под неё), справа восемь образцов палитры. На
/// узком окне холсту остаётся не меньше <see cref="CanvasMin"/>: сначала сужается правая
/// панель, потом левая. Запомненная ширина при этом не меняется - окно расширили, и
/// панели вернулись.
/// </summary>
public static class PanelWidths
{
    public const double LeftMin = 88, LeftDefault = 142, LeftMax = 208;
    public const double RightMin = 260, RightDefault = 290, RightMax = 420;

    /// <summary>Сколько холсту остаётся на узком окне.</summary>
    public const double CanvasMin = 300;

    /// <summary>Поля окна и зазоры между панелями и холстом.</summary>
    public const double Chrome = 40;

    public static double ClampLeft(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, LeftMin, LeftMax) : LeftDefault;

    public static double ClampRight(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, RightMin, RightMax) : RightDefault;

    /// <summary>Ширины, которые встанут на окне шириной <paramref name="windowWidth"/>.</summary>
    public static (double Left, double Right) Fit(double left, double right, double windowWidth)
    {
        // Целые пиксели и вниз: тогда уложенное укладывается ровно так же ещё раз, без
        // дрожи в последних знаках, и сумма панелей не залезает на место холста.
        left = Math.Floor(ClampLeft(left));
        right = Math.Floor(ClampRight(right));
        if (!double.IsFinite(windowWidth)) return (left, right);
        var room = windowWidth - Chrome - CanvasMin;
        if (left + right > room) right = Math.Max(RightMin, Math.Floor(room - left));
        if (left + right > room) left = Math.Max(LeftMin, Math.Floor(room - right));
        return (left, right);
    }

    public static string Format(double left, double right) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(left)};{Math.Round(right)}");

    /// <summary>«142;290». Что угодно другое - null, и панели встают по умолчанию.</summary>
    public static (double Left, double Right)? Parse(string? text)
    {
        var parts = text?.Trim().Split(';');
        if (parts is not { Length: 2 }) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
            || !double.IsFinite(l) || !double.IsFinite(r)) return null;
        return (ClampLeft(l), ClampRight(r));
    }

    /// <summary>Файл настроек; null - не читать и не писать (так делает программа кадров).</summary>
    public static string? FilePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaintPro", "panels.txt");

    public static (double Left, double Right) Load()
    {
        try
        {
            if (FilePath is not null && File.Exists(FilePath) && Parse(File.ReadAllText(FilePath)) is { } saved)
                return saved;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return (LeftDefault, RightDefault);
    }

    public static void Save(double left, double right)
    {
        if (FilePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Format(left, right));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не записалось - в следующий раз панели встанут по умолчанию.
        }
    }
}
