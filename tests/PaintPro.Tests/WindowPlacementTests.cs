using System.IO;
using System.Windows;
using System.Windows.Interop;
using PaintPro.Services;
using Xunit;
using static PaintPro.Services.WindowPlacement;

namespace PaintPro.Tests;

/// <summary>
/// Размер и место окна между запусками (1.28.0). То же правило, что у калькуляторов:
/// что бы ни лежало в файле, окно открывается там, где его видно и можно взять за
/// заголовок. Единицы - пиксели экрана (Paint понимает масштаб каждого монитора).
/// </summary>
public class WindowPlacementTests
{
    private const double W = 1380, H = 860, MinW = 900, MinH = 600;
    private static readonly Area FullHd = new(0, 0, 1920, 1040);
    private static readonly Area Right = new(1920, 0, 2560, 1400);

    private static Placement R(Placement? saved, params Area[] screens) => Restore(saved, screens, W, H, MinW, MinH);

    [Fact]
    public void Nothing_saved_gives_the_default_size_centred() =>
        Assert.Equal(new Placement(null, null, W, H, false), R(null, FullHd));

    [Fact]
    public void A_window_saved_on_screen_opens_exactly_where_it_was()
    {
        var saved = new Placement(100, 50, 1200, 800, true);
        Assert.Equal(saved, R(saved, FullHd));
    }

    [Fact]
    public void The_second_monitor_unplugged_centres_the_window_and_keeps_its_size()
    {
        var saved = new Placement(2200, 100, 1600, 1000, false);
        Assert.Equal(saved, R(saved, FullHd, Right));
        Assert.Equal(new Placement(null, null, 1600, 1000, false), R(saved, FullHd));
    }

    [Fact]
    public void A_window_larger_than_its_screen_is_cut_to_it_and_pulled_on()
    {
        Assert.Equal(new Placement(0, 0, 1920, 1040, false), R(new Placement(-100, 10, 2400, 1400, false), FullHd));
    }

    [Fact]
    public void On_a_screen_lower_than_the_minimum_window_the_title_bar_stays_on_it() =>
        Assert.Equal(0, R(new Placement(0, 300, 1000, 700, false), new Area(0, 0, 1024, 560)).Top);

    // Случайные раскладки мониторов (в ряд, без перекрытий, как настоящие) и случайные
    // сохранённые числа, в том числе NaN. Сид в названии провала - чтобы повторить.
    private static Area[] RandomScreens(Random rnd)
    {
        var x = rnd.Next(-5000, 5000);
        return Enumerable.Range(0, rnd.Next(1, 5)).Select(_ =>
        {
            var a = new Area(x, rnd.Next(-3000, 3000), rnd.Next(640, 5000), rnd.Next(480, 3000));
            x += (int)a.Width;
            return a;
        }).ToArray();
    }

    private static Placement RandomSaved(Random rnd)
    {
        double? Coord() => rnd.Next(4) switch { 0 => null, 1 => double.NaN, _ => rnd.Next(-20000, 20000) };
        var w = rnd.Next(5) == 0 ? double.NaN : rnd.Next(-100, 10000);
        return new Placement(Coord(), Coord(), w, rnd.Next(-100, 10000), rnd.Next(2) == 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Whatever_was_saved_the_window_is_sane_its_title_bar_on_a_screen_and_restoring_again_changes_nothing(int seed)
    {
        var rnd = new Random(seed * 7919);
        for (var i = 0; i < 3000; i++)
        {
            var screens = RandomScreens(rnd);
            var saved = RandomSaved(rnd);
            var p = R(saved, screens);
            var where = $"сид {seed}, шаг {i}: {saved} на [{string.Join(", ", screens)}] -> {p}";
            Assert.True(p.Width >= MinW && p.Height >= MinH && double.IsFinite(p.Width) && double.IsFinite(p.Height), where);
            Assert.True(p.Left is null == p.Top is null, where);
            if (p.Left is null) continue;
            Assert.True(screens.Any(a => p.Left >= a.X && p.Top >= a.Y && p.Left < a.X + a.Width && p.Top + GripHeight <= a.Y + a.Height), where);
            Assert.Equal(p, R(p, screens));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1;2;3")]
    [InlineData("a;b;c;d;max")]
    [InlineData("1;2;3;4;sometimes")]
    public void Garbage_in_the_file_reads_as_nothing(string? text) => Assert.Null(Parse(text));

    [Fact]
    public void Format_and_parse_are_a_pair_in_any_culture()
    {
        var saved = new Placement(10.5, -20, 1400, 900, true);
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            Assert.Equal(saved, Parse(Format(saved)));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }

    /// <summary>
    /// На настоящем окне WPF: сохранённые границы в файле -> окно (ещё не показанное)
    /// встаёт ровно туда, а CurrentBounds читает их обратно.
    /// </summary>
    [Fact]
    public void A_real_window_opens_where_the_file_says()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var file = Path.Combine(Path.GetTempPath(), $"paint-window-{Environment.ProcessId}.txt");
            var before = WindowPlacementService.FilePath;
            try
            {
                // Место и размер - от настоящего экрана: у раннера GitHub он 1024x768, и
                // окно 1000x700 со сдвигом 40 не помещалось - правило законно двигало его к краю.
                var main = WindowPlacementService.Screens()[0];
                var want = new Area(main.X + 20, main.Y + 20, Math.Min(1000, main.Width - 40), Math.Min(700, main.Height - 40));
                WindowPlacementService.FilePath = file;
                WindowPlacementService.Save(new Placement(want.X, want.Y, want.Width, want.Height, false));

                var window = new Window { Width = 1380, Height = 860, MinWidth = 300, MinHeight = 200, ShowActivated = false };
                new WindowInteropHelper(window).EnsureHandle();
                var set = WindowPlacementService.Restore(window);
                Assert.Equal(want, set);
                Assert.Equal(want, WindowPlacementService.CurrentBounds(window));
                window.Close();
            }
            catch (Exception e) { failure = e; }
            finally
            {
                WindowPlacementService.FilePath = before;
                File.Delete(file);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
