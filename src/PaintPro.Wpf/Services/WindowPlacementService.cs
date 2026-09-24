using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaintPro.Services;

/// <summary>
/// Размер и место окна между запусками: %APPDATA%\PaintPro\window.txt. Правило выбора
/// места - <see cref="WindowPlacement"/>; здесь файл, мониторы и само окно.
///
/// Всё в настоящих пикселях экрана: Paint понимает масштаб каждого монитора отдельно
/// (PerMonitorV2), и единицы WPF на разных мониторах разные. Окно ставится через
/// SetWindowPos до показа, а его обычные границы запоминаются GetWindowRect.
/// </summary>
public static class WindowPlacementService
{
    /// <summary>Файл настроек; null - не читать и не писать (так делает программа кадров).</summary>
    public static string? FilePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaintPro", "window.txt");

    public static WindowPlacement.Placement? Load()
    {
        if (FilePath is null) return null;
        try { return File.Exists(FilePath) ? WindowPlacement.Parse(File.ReadAllText(FilePath)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Запись через временный файл: убитый посреди записи процесс оставит старый файл.</summary>
    public static void Save(WindowPlacement.Placement placement)
    {
        if (FilePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, WindowPlacement.Format(placement));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не записалось - в следующий раз окно откроется по умолчанию.
        }
    }

    /// <summary>Рабочие области мониторов в пикселях, основной первым.</summary>
    public static IReadOnlyList<WindowPlacement.Area> Screens()
    {
        var list = new List<(WindowPlacement.Area area, bool primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Rect32 _, IntPtr _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var w = info.Work;
                list.Add((new WindowPlacement.Area(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top), (info.Flags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.primary).Select(m => m.area).ToList();
    }

    /// <summary>
    /// Поставить окно по сохранённому: вызывается из OnSourceInitialized, когда окно уже
    /// есть, но ещё не показано. Нет сохранённого - окно остаётся как в разметке.
    /// Возвращает обычные границы, которые поставило (у развёрнутого - те, к которым оно вернётся).
    /// </summary>
    public static WindowPlacement.Area? Restore(Window window)
    {
        var saved = Load();
        if (saved is null) return null;
        var hwnd = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelper.GetDpi(window);
        var screens = Screens();
        var p = WindowPlacement.Restore(saved, screens,
            window.Width * dpi.DpiScaleX, window.Height * dpi.DpiScaleY,
            window.MinWidth * dpi.DpiScaleX, window.MinHeight * dpi.DpiScaleY);

        double x, y;
        if (p.Left is { } left && p.Top is { } top) (x, y) = (left, top);
        else
        {
            // Без места - по центру основного монитора, с сохранённым размером.
            var main = screens.Count > 0 ? screens[0] : new WindowPlacement.Area(0, 0, p.Width, p.Height);
            x = main.X + Math.Max(0, (main.Width - p.Width) / 2);
            y = main.Y + Math.Max(0, (main.Height - p.Height) / 2);
        }
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        SetWindowPos(hwnd, IntPtr.Zero, (int)x, (int)y, (int)p.Width, (int)p.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        if (p.Maximized) window.WindowState = WindowState.Maximized;
        return new WindowPlacement.Area(x, y, p.Width, p.Height);
    }

    /// <summary>Границы окна в пикселях сейчас (для обычного, не развёрнутого окна).</summary>
    public static WindowPlacement.Area? CurrentBounds(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        return new WindowPlacement.Area(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect32 Monitor; public Rect32 Work; public uint Flags; }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect32 rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
