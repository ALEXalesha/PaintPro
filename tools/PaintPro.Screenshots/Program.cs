// Кадры для README C#-версии: собираются программой, а не снимком экрана.
//
//   dotnet run --project tools\PaintPro.Screenshots
//
// Настоящее окно MainWindow со стилями и темами приложения. Картинка рисуется
// настоящими инструментами тем же путём, каким их ведёт холст на мышь:
// ActiveToolInstance.OnPointerDown/Move/Up с ToolContext модели, так что каждый
// штрих проходит через команды и ленту истории. Окно открывается далеко за краем
// экрана и рисуется в кадр само (RenderTargetBitmap): чужое окно в снимок попасть не
// может. Тема ставится только на экран, файл настроек (ThemeService.Save) не трогается.
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaintPro;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.ViewModels;
using SkiaSharp;

internal static class Program
{
    private const double Scale = 1.0;

    [STAThread]
    private static int Main()
    {
        var output = Path.Combine(FindRepo(), "docs", "screenshots");
        Directory.CreateDirectory(output);

        var app = new App();
        app.InitializeComponent(); // ресурсы и тема из App.xaml; Run() не нужен
        ThemeService.Apply(ThemeService.DefaultId);
        // Окно в размере по умолчанию: сохранённый размер человека не читается и не пишется.
        WindowPlacementService.FilePath = null;

        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        window.Show();
        var vm = (MainViewModel)window.DataContext;
        Wait(800);

        Draw(vm);

        foreach (var (theme, name) in new[] { ("Glass", "hero.png"), ("Light", "theme-light.png"), ("Night", "theme-night.png") })
        {
            ThemeService.Apply(theme);
            Save(window, output, name);
        }

        // Окно выбора цвета - отдельным кадром, тоже далеко за краем экрана.
        ThemeService.Apply(ThemeService.DefaultId);
        var picker = new PaintPro.Views.ColorPickerDialog(SKColor.Parse("#EF476F"))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
        };
        picker.Show();
        Wait(500);
        Save(picker, output, "color-picker.png");
        picker.Close();

        // Окна сообщения и ввода - для просмотра глазами, во временную папку, не в README.
        var review = Path.Combine(Path.GetTempPath(), "paint-review");
        Directory.CreateDirectory(review);
        foreach (var (dialog, name) in new (Window, string)[]
                 {
                     (new PaintPro.Views.GlassMessage("Рисунок изменён. Сохранить перед выходом?", "Paint Pro", MessageBoxButton.YesNoCancel), "message.png"),
                     (new PaintPro.Views.PromptDialog("Введите текст:", "Текст", "Привет"), "prompt.png"),
                     (new PaintPro.Views.TextDialog("Привет, мир"), "text.png"),
                 })
        {
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = -20000; dialog.Top = -20000; dialog.ShowInTaskbar = false; dialog.ShowActivated = false;
            dialog.Show();
            Wait(400);
            Save(dialog, review, name);
            dialog.Close();
        }

        // Не window.Close(): рисунок изменён, и окно спросило бы «Сохранить перед
        // выходом?» настоящим MessageBox поверх экрана.
        Environment.Exit(0);
        return 0;
    }

    /// <summary>Домик под солнцем: заливка, фигуры с заливкой, кисть, второй слой.</summary>
    private static void Draw(MainViewModel vm)
    {
        vm.ShapeFill = true;
        vm.ToolSize = 3; // фигуры обводятся размером инструмента

        Tool(vm, ToolKind.Fill, "#9CD3F5");
        Click(vm, 450, 200);

        Tool(vm, ToolKind.Rect, "#7BC47F");
        Drag(vm, (-10, 430), (910, 610));

        Tool(vm, ToolKind.Ellipse, "#FFD166");
        Drag(vm, (690, 50), (810, 170));

        Tool(vm, ToolKind.Rect, "#E07A5F");
        Drag(vm, (170, 290), (400, 470));
        Tool(vm, ToolKind.Triangle, "#9E2A2B");
        Drag(vm, (150, 170), (420, 292));
        Tool(vm, ToolKind.Rect, "#6D4C41");
        Drag(vm, (255, 370), (315, 470));
        Tool(vm, ToolKind.Rect, "#F1FAEE");
        Drag(vm, (195, 320), (240, 360));
        Drag(vm, (335, 320), (380, 360));

        // Облака кистью.
        Tool(vm, ToolKind.Brush, "#FFFFFF", size: 34);
        Stroke(vm, Wave(470, 90, 180, 8));
        Stroke(vm, Wave(90, 70, 150, 7));

        // Второй слой: цветы и звезда поверх.
        vm.AddLayerCommand.Execute(null);
        Tool(vm, ToolKind.Heart, "#EF476F", size: 3);
        Drag(vm, (560, 470), (620, 530));
        Drag(vm, (660, 500), (700, 540));
        Tool(vm, ToolKind.Star, "#FFB703");
        Drag(vm, (740, 440), (820, 520));
        Tool(vm, ToolKind.Pencil, "#2D6A4F", size: 4);
        Stroke(vm, new[] { (590f, 530f), (592f, 560f), (590f, 590f) });
        Stroke(vm, new[] { (680f, 540f), (681f, 565f), (680f, 590f) });

        Tool(vm, ToolKind.Brush, "#EF476F", size: 12);
    }

    private static void Tool(MainViewModel vm, ToolKind kind, string color, int? size = null)
    {
        vm.ActiveTool = kind;
        vm.PrimaryColor = SKColor.Parse(color);
        if (size is int s) vm.ToolSize = s;
    }

    private static void Click(MainViewModel vm, float x, float y) => Stroke(vm, new[] { (x, y) });

    private static void Drag(MainViewModel vm, (float x, float y) from, (float x, float y) to) =>
        Stroke(vm, new[] { from, to });

    private static void Stroke(MainViewModel vm, IReadOnlyList<(float x, float y)> points)
    {
        var tool = vm.ActiveToolInstance;
        tool.OnPointerDown(new SKPoint(points[0].x, points[0].y), vm.ToolContext);
        foreach (var (x, y) in points.Skip(1)) tool.OnPointerMove(new SKPoint(x, y), vm.ToolContext);
        var last = points[^1];
        tool.OnPointerUp(new SKPoint(last.x, last.y), vm.ToolContext);
        Wait(40);
    }

    /// <summary>Волнистая линия слева направо: облако из одного штриха кисти.</summary>
    private static (float x, float y)[] Wave(float x0, float y0, float width, int bumps) =>
        Enumerable.Range(0, bumps * 6 + 1)
            .Select(i => (x0 + width * i / (bumps * 6f), y0 + 10f * MathF.Sin(i * MathF.PI / 3f)))
            .ToArray();

    private static void Save(Window window, string folder, string name)
    {
        Wait(700);
        var content = (FrameworkElement)window.Content;
        // С полями вокруг содержимого: у окна выбора цвета они под тень, и без них кадр
        // выходил сдвинутым и обрезанным справа.
        var m = content.Margin;
        var size = new Rect(0, 0, content.ActualWidth + m.Left + m.Right, content.ActualHeight + m.Top + m.Bottom);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, size);
            dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = size }, null, size);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Round(size.Width * Scale), (int)Math.Round(size.Height * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(folder, name))) encoder.Save(file);
        Console.WriteLine($"  {name}");
    }

    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PaintPro.sln"))) return dir.FullName;
        throw new InvalidOperationException("PaintPro.sln не найден выше " + AppContext.BaseDirectory);
    }
}
