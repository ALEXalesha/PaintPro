using System.IO;
using System.Text.RegularExpressions;
using PaintPro.Services;
using PaintPro.ViewModels;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Ширина боковых панелей (1.28.0) и то, что вместе с ней сделано одинаковым с
/// Electron-версией: палитра и поля размера холста. Правило ширины то же, что там
/// (paint-pro.html, PANELS и fitPanels) - проверка ниже сверяет числа с файлом.
/// </summary>
public class PanelWidthsTests
{
    [Theory]
    [InlineData(10, PanelWidths.LeftMin)]
    [InlineData(100, 100)]
    [InlineData(1000, PanelWidths.LeftMax)]
    [InlineData(double.NaN, PanelWidths.LeftDefault)]
    [InlineData(double.PositiveInfinity, PanelWidths.LeftDefault)]
    public void The_left_panel_stays_within_its_bounds(double asked, double expected) =>
        Assert.Equal(expected, PanelWidths.ClampLeft(asked));

    [Theory]
    [InlineData(10, PanelWidths.RightMin)]
    [InlineData(333, 333)]
    [InlineData(1000, PanelWidths.RightMax)]
    [InlineData(double.NaN, PanelWidths.RightDefault)]
    public void The_right_panel_stays_within_its_bounds(double asked, double expected) =>
        Assert.Equal(expected, PanelWidths.ClampRight(asked));

    [Fact]
    public void On_a_wide_window_the_panels_are_what_was_asked()
    {
        Assert.Equal((200.0, 400.0), PanelWidths.Fit(200, 400, 1920));
    }

    [Fact]
    public void On_a_narrow_window_the_right_panel_gives_way_first_then_the_left()
    {
        // 900 - 40 - 300 = 560 на обе панели.
        Assert.Equal((200.0, 360.0), PanelWidths.Fit(200, 400, 900));
        // Правой некуда дальше 260 - сужается левая.
        Assert.Equal((200.0, 260.0), PanelWidths.Fit(208, 260, 800));
        Assert.Equal((PanelWidths.LeftMin, PanelWidths.RightMin), PanelWidths.Fit(208, 420, 500));
    }

    [Fact]
    public void Whatever_is_asked_the_panels_are_within_bounds_and_leave_the_canvas_room_when_they_can()
    {
        var rnd = new Random(1280);
        for (var i = 0; i < 5000; i++)
        {
            var l = rnd.NextDouble() * 600 - 100;
            var r = rnd.NextDouble() * 800 - 100;
            var w = 300 + rnd.NextDouble() * 3000;
            var (fl, fr) = PanelWidths.Fit(l, r, w);
            var ctx = $"{l} {r} {w}";
            Assert.InRange(fl, PanelWidths.LeftMin, PanelWidths.LeftMax);
            Assert.InRange(fr, PanelWidths.RightMin, PanelWidths.RightMax);
            var fitsAtAll = w - PanelWidths.Chrome - PanelWidths.CanvasMin >= PanelWidths.LeftMin + PanelWidths.RightMin;
            if (fitsAtAll) Assert.True(w - PanelWidths.Chrome - fl - fr >= PanelWidths.CanvasMin - 1e-9, ctx);
            // Уже уложенное укладывается так же.
            Assert.Equal((fl, fr), PanelWidths.Fit(fl, fr, w));
            // Панель не шире, чем просили.
            Assert.True(fl <= PanelWidths.ClampLeft(l) && fr <= PanelWidths.ClampRight(r), ctx);
            Assert.True(fl == Math.Floor(fl) && fr == Math.Floor(fr), ctx);
        }
    }

    [Theory]
    [InlineData("142;290", 142, 290)]
    [InlineData(" 100;300 ", 100, 300)]
    [InlineData("5;9000", PanelWidths.LeftMin, PanelWidths.RightMax)]
    public void The_file_reads_back(string text, double l, double r) =>
        Assert.Equal((l, r), PanelWidths.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("142")]
    [InlineData("a;b")]
    [InlineData("1;2;3")]
    [InlineData("NaN;290")]
    public void A_broken_file_reads_as_nothing(string? text) => Assert.Null(PanelWidths.Parse(text));

    [Fact]
    public void Save_then_load_gives_the_same_widths()
    {
        var old = PanelWidths.FilePath;
        var dir = Directory.CreateTempSubdirectory("panels-");
        try
        {
            PanelWidths.FilePath = Path.Combine(dir.FullName, "sub", "panels.txt");
            Assert.Equal((PanelWidths.LeftDefault, PanelWidths.RightDefault), PanelWidths.Load());
            PanelWidths.Save(100, 350);
            Assert.Equal((100.0, 350.0), PanelWidths.Load());
            File.WriteAllText(PanelWidths.FilePath, "мусор");
            Assert.Equal((PanelWidths.LeftDefault, PanelWidths.RightDefault), PanelWidths.Load());
        }
        finally
        {
            PanelWidths.FilePath = old;
            dir.Delete(true);
        }
    }

    // ───────── то же, что в Electron-версии ─────────

    private static string Electron() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "paint-pro-electron", "paint-pro.html"));

    [Fact]
    public void The_panel_limits_are_the_same_numbers_as_in_the_Electron_version()
    {
        var m = Regex.Match(Electron(), @"const PANELS = \{([^}]*)\}");
        Assert.True(m.Success, "PANELS не найден в paint-pro.html");
        double Get(string key) => double.Parse(Regex.Match(m.Groups[1].Value, key + @":\s*(\d+)").Groups[1].Value);
        Assert.Equal(PanelWidths.LeftMin, Get("leftMin"));
        Assert.Equal(PanelWidths.LeftDefault, Get("leftDefault"));
        Assert.Equal(PanelWidths.LeftMax, Get("leftMax"));
        Assert.Equal(PanelWidths.RightMin, Get("rightMin"));
        Assert.Equal(PanelWidths.RightDefault, Get("rightDefault"));
        Assert.Equal(PanelWidths.RightMax, Get("rightMax"));
        Assert.Equal(PanelWidths.CanvasMin, Get("canvasMin"));
        Assert.Equal(PanelWidths.Chrome, Get("chrome"));
    }

    [Fact]
    public void The_palette_is_the_same_forty_colours_in_the_same_order_as_in_the_Electron_version()
    {
        var block = Regex.Match(Electron(), @"const colors = \[(.*?)\];", RegexOptions.Singleline).Groups[1].Value;
        var electron = Regex.Matches(block, "'(#[0-9a-fA-F]{6})'").Select(x => x.Groups[1].Value.ToUpperInvariant()).ToArray();
        Assert.Equal(40, electron.Length);
        Assert.Equal(electron, MainViewModel.PaletteHexes);
        Assert.Equal(40, new MainViewModel().Palette.Count);
    }

    // ───────── поля размера холста ─────────

    [Fact]
    public void The_size_fields_show_the_canvas_and_follow_it()
    {
        var vm = new MainViewModel();
        Assert.Equal(("900", "600"), (vm.CanvasWidthText, vm.CanvasHeightText));
        vm.CanvasWidthText = "400";
        vm.CanvasHeightText = " 300 ";
        vm.ApplyCanvasSizeCommand.Execute(null);
        Assert.Equal((400, 300), (vm.Document.CanvasWidth, vm.Document.CanvasHeight));
        vm.UndoCommand.Execute(null);
        Assert.Equal(("900", "600"), (vm.CanvasWidthText, vm.CanvasHeightText));
    }

    [Fact]
    public void The_same_size_in_the_fields_is_not_an_edit()
    {
        var vm = new MainViewModel();
        var before = vm.Document.History.Commands.Count;
        vm.ApplyCanvasSizeCommand.Execute(null);
        Assert.Equal(before, vm.Document.History.Commands.Count);
        Assert.False(vm.IsDirty);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaintPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
