using PaintPro.Models;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Размер кисти, изменённый колесом ПОСРЕДИ штриха (1.31.0).
///
/// Толщину штрих брал один раз, на нажатии, и рисовал ею до отпускания: крутишь колесо,
/// ползунок и кружок курсора меняются, а линия идёт прежней толщины. Новая начиналась
/// только со следующего нажатия. Теперь толщина берётся заново на каждом движении, и
/// изменение видно сразу, прямо в том же штрихе.
/// </summary>
public class LiveToolSizeTests
{
    private static MainViewModel Vm(ToolKind tool, int size)
        => new() { ActiveTool = tool, ToolSize = size };

    private static SKBitmap Paper(MainViewModel vm) => ((PixelLayer)vm.Document.Layers[0]).Bitmap;

    private static void Down(MainViewModel vm, float x, float y) => vm.ActiveToolInstance.OnPointerDown(new SKPoint(x, y), vm.ToolContext);
    private static void Move(MainViewModel vm, float x, float y) => vm.ActiveToolInstance.OnPointerMove(new SKPoint(x, y), vm.ToolContext);
    private static void Up(MainViewModel vm, float x, float y) => vm.ActiveToolInstance.OnPointerUp(new SKPoint(x, y), vm.ToolContext);

    /// <summary>Крутить колесо, пока размер не станет не меньше заданного.</summary>
    private static void WheelUpTo(MainViewModel vm, int size)
    {
        for (int i = 0; i < 200 && vm.ToolSize < size; i++) vm.AdjustToolSize(up: true);
        Assert.True(vm.ToolSize >= size, $"колесом дошли только до {vm.ToolSize}");
    }

    private static void WheelDownTo(MainViewModel vm, int size)
    {
        for (int i = 0; i < 200 && vm.ToolSize > size; i++) vm.AdjustToolSize(up: false);
        Assert.True(vm.ToolSize <= size, $"колесом дошли только до {vm.ToolSize}");
    }

    private static bool Inked(SKBitmap b, int x, int y) => b.GetPixel(x, y) != SKColors.White;

    /// <summary>
    /// Горизонтальный штрих: первая половина тонкая, посередине колесо вверх, вторая
    /// половина толстая. Возвращает размер после колеса.
    /// </summary>
    private static int ThinThenThick(MainViewModel vm, bool release = true)
    {
        Down(vm, 100, 300);
        for (int x = 110; x <= 300; x += 10) Move(vm, x, 300);
        WheelUpTo(vm, 40);
        for (int x = 310; x <= 500; x += 10) Move(vm, x, 300);
        if (release) Up(vm, 500, 300);
        return vm.ToolSize;
    }

    [Theory]
    [InlineData(ToolKind.Brush)]
    [InlineData(ToolKind.Marker)]
    [InlineData(ToolKind.Eraser)]
    // колесо посреди штриха: дальше линия идёт новой толщиной, в том же штрихе
    public void the_stroke_follows_the_wheel_without_releasing_the_button(ToolKind tool)
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm(tool, 4);
            if (tool == ToolKind.Eraser) Paper(vm).Erase(SKColors.Black);
            ThinThenThick(vm);
            var b = Paper(vm);
            bool Touched(int x, int y) => tool == ToolKind.Eraser
                ? b.GetPixel(x, y) != SKColors.Black
                : Inked(b, x, y);

            // Первая половина тонкая: в 10 пикселях от линии пусто.
            Assert.True(Touched(200, 300));
            Assert.False(Touched(200, 310));
            // Вторая - толстая: в 15 пикселях от линии уже закрашено.
            Assert.True(Touched(400, 315));
            Assert.True(Touched(400, 285));
            Assert.False(Touched(400, 330));
        });
    }

    [Fact]
    // карандаш тоже следует за колесом; толщина у него - сам размер (1.32.0)
    public void the_pencil_follows_the_wheel()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm(ToolKind.Pencil, 4);
            int size = ThinThenThick(vm);
            var b = Paper(vm);
            Assert.False(Inked(b, 200, 306));
            Assert.True(Inked(b, 400, 300 + size / 2 - 2));
            Assert.False(Inked(b, 400, 300 + size / 2 + 3));
        });
    }

    [Fact]
    // колесо вниз посреди штриха - линия становится тоньше
    public void the_stroke_gets_thinner_when_the_wheel_goes_down()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm(ToolKind.Brush, 40);
            Down(vm, 100, 300);
            for (int x = 110; x <= 300; x += 10) Move(vm, x, 300);
            WheelDownTo(vm, 4);
            for (int x = 310; x <= 500; x += 10) Move(vm, x, 300);
            Up(vm, 500, 300);
            var b = Paper(vm);
            Assert.True(Inked(b, 200, 315));
            Assert.True(Inked(b, 400, 300));
            Assert.False(Inked(b, 400, 310));
        });
    }

    [Fact]
    // штрих с колесом посередине - одна запись в истории, отмена снимает его целиком
    public void a_stroke_with_a_wheel_in_the_middle_is_one_history_entry()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm(ToolKind.Brush, 4);
            int before = vm.Document.History.UndoDepth;
            ThinThenThick(vm);
            Assert.Equal(before + 1, vm.Document.History.UndoDepth);
            vm.UndoCommand.Execute(null);
            var b = Paper(vm);
            Assert.False(Inked(b, 200, 300));
            Assert.False(Inked(b, 400, 300));
            vm.RedoCommand.Execute(null);
            Assert.True(Inked(b, 200, 300));
            Assert.True(Inked(b, 400, 315));
        });
    }

    [Fact]
    // пока кнопка зажата, превью уже показывает новую толщину - не после отпускания
    public void the_preview_shows_the_new_width_before_release()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm(ToolKind.Brush, 4);
            ThinThenThick(vm, release: false);
            var preview = vm.ActiveToolInstance.PreviewBitmap;
            Assert.NotNull(preview);
            Assert.True(preview!.GetPixel(400, 315).Alpha > 0, "толстая часть в превью");
            Assert.Equal(0, preview.GetPixel(200, 310).Alpha);
            Up(vm, 500, 300);
        });
    }

    [Fact]
    // цвет посреди штриха не меняется - колесо трогает только толщину
    public void the_wheel_changes_only_the_width_not_the_colour()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm(ToolKind.Brush, 4);
            vm.PrimaryColor = SKColors.Red;
            Down(vm, 100, 300);
            for (int x = 110; x <= 300; x += 10) Move(vm, x, 300);
            vm.PrimaryColor = SKColors.Blue;
            WheelUpTo(vm, 30);
            for (int x = 310; x <= 500; x += 10) Move(vm, x, 300);
            Up(vm, 500, 300);
            Assert.Equal(SKColors.Red, Paper(vm).GetPixel(400, 300));
        });
    }

    [Fact]
    // следующий штрих начинается уже с нового размера, как и раньше
    public void the_next_stroke_starts_with_the_new_size()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm(ToolKind.Brush, 4);
            ThinThenThick(vm);
            Down(vm, 100, 500);
            for (int x = 110; x <= 300; x += 10) Move(vm, x, 500);
            Up(vm, 300, 500);
            Assert.True(Inked(Paper(vm), 200, 515));
        });
    }
}

/// <summary>
/// Предел размера кисти (1.31.0): 300 вместо 100, в обеих версиях одинаково.
/// </summary>
public class ToolSizeLimitTests
{
    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PaintPro.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    // предел - 300, втрое больше прежних 100
    public void the_largest_tool_size_is_three_hundred()
    {
        Assert.Equal(300, MainViewModel.MaxToolSize);
        Assert.Equal(1, MainViewModel.MinToolSize);
    }

    [Fact]
    // в Electron-версии тот же предел у ползунка размера
    public void the_electron_version_has_the_same_limit()
    {
        var html = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "paint-pro-electron", "paint-pro.html"));
        var m = System.Text.RegularExpressions.Regex.Match(html, @"<input type=""range"" id=""size-input"" min=""(\d+)"" max=""(\d+)""");
        Assert.True(m.Success, "size-input не найден");
        Assert.Equal(MainViewModel.MinToolSize, int.Parse(m.Groups[1].Value));
        Assert.Equal(MainViewModel.MaxToolSize, int.Parse(m.Groups[2].Value));
    }

    [Fact]
    // колесо доходит до 300 и там объясняет предел
    public void the_wheel_reaches_three_hundred_and_explains_the_limit()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel { ActiveTool = ToolKind.Brush, ToolSize = 100 };
            for (int i = 0; i < 100; i++) vm.AdjustToolSize(up: true);
            Assert.Equal(300, vm.ToolSize);
            Assert.False(vm.AdjustToolSize(up: true));
            Assert.Contains("300 px", vm.StatusHint);
        });
    }

    [Fact]
    // ползунок размера в окне тоже до 300
    public void the_size_slider_goes_to_three_hundred()
    {
        var vm = new MainViewModel();
        Assert.Equal(300, vm.ToolSizeMax);
    }

    [Theory]
    [InlineData(ToolKind.Brush)]
    [InlineData(ToolKind.Eraser)]
    // кисть и ластик на 300 рисуют полосу шириной 300
    public void a_three_hundred_pixel_tool_draws_a_three_hundred_pixel_band(ToolKind tool)
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel { ActiveTool = tool, ToolSize = 300 };
            var paper = ((PixelLayer)vm.Document.Layers[0]).Bitmap;
            if (tool == ToolKind.Eraser) paper.Erase(SKColors.Black);
            var t = vm.ActiveToolInstance;
            t.OnPointerDown(new SKPoint(100, 300), vm.ToolContext);
            t.OnPointerMove(new SKPoint(800, 300), vm.ToolContext);
            t.OnPointerUp(new SKPoint(800, 300), vm.ToolContext);
            var untouched = tool == ToolKind.Eraser ? SKColors.Black : SKColors.White;
            Assert.NotEqual(untouched, paper.GetPixel(450, 300 - 145));
            Assert.NotEqual(untouched, paper.GetPixel(450, 300 + 145));
            Assert.Equal(untouched, paper.GetPixel(450, 300 - 155));
            Assert.Equal(untouched, paper.GetPixel(450, 300 + 155));
        });
    }
}
