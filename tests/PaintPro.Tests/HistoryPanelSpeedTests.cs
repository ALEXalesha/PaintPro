using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaintPro.Models;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;
using IOPath = System.IO.Path;

namespace PaintPro.Tests;

/// <summary>
/// Лента истории не подвисает на быстрых мазках (1.34.0).
///
/// Лента пересоздавала все строки на каждую правку, и список не был виртуальным: на ста
/// тридцати правках это 10 мс на строки и 58 мс на раскладку после каждого мазка, и с каждым
/// мазком больше. Быстрые мазки подряд (большая кисть, смена цвета, ластик - карандаш)
/// подвисали. Теперь строки правятся на месте, а создаются только видимые.
/// </summary>
public class HistoryPanelSpeedTests
{
    private static MainViewModel Vm() => new() { ActiveTool = ToolKind.Pencil, ToolSize = 20 };

    private static void Stroke(MainViewModel vm, int i)
    {
        var t = vm.ActiveToolInstance;
        float y = 20 + i % 500;
        // Цвет чередуется: мазок поверх такого же ничего не меняет, и лента его не записывает.
        vm.ToolContext.PrimaryColor = i % 2 == 0 ? SKColors.Red : SKColors.Blue;
        t.OnPointerDown(new SKPoint(20, y), vm.ToolContext);
        t.OnPointerMove(new SKPoint(200, y + 3), vm.ToolContext);
        t.OnPointerUp(new SKPoint(200, y + 3), vm.ToolContext);
    }

    /// <summary>Строки так, как их построила бы лента с нуля, - эталон для сверки.</summary>
    private static List<(int Target, string Label, object? Cmd, bool Current, bool Future, bool Enabled, bool CanToggle)> Expected(MainViewModel vm)
    {
        var h = vm.Document.History;
        var list = new List<(int, string, object?, bool, bool, bool, bool)>
        {
            (0, h.Trimmed ? "Дальше отмена не идёт" : "Исходное состояние", null, h.Cursor == 0, false, true, false),
        };
        for (int i = 0; i < h.Commands.Count; i++)
        {
            var c = h.Commands[i];
            list.Add((i + 1, null!, c, i + 1 == h.Cursor, i + 1 > h.Cursor, h.IsEnabled(c), h.CanToggle(c)));
        }
        return list;
    }

    private static void AssertInStep(MainViewModel vm)
    {
        var want = Expected(vm);
        Assert.Equal(want.Count, vm.HistoryItems.Count);
        for (int i = 0; i < want.Count; i++)
        {
            var row = vm.HistoryItems[i];
            var w = want[i];
            Assert.Equal(w.Target, row.Target);
            if (w.Label is not null) Assert.Equal(w.Label, row.Label);
            Assert.Same(w.Cmd, row.Command);
            Assert.Equal(w.Current, row.IsCurrent);
            Assert.Equal(w.Future, row.IsFuture);
            if (w.Cmd is not null)
            {
                Assert.Equal(w.Enabled, row.Enabled);
                Assert.Equal(w.CanToggle, row.CanToggle);
                Assert.False(string.IsNullOrEmpty(row.ToggleHint));
                Assert.Equal(w.CanToggle, row.ToggleHint.StartsWith("Выключить эту правку"));
            }
        }
    }

    // ───────── строки правятся на месте ─────────

    [Fact]
    // новый мазок - одна новая строка, прежние - те же объекты
    public void a_new_stroke_adds_one_row_and_keeps_the_others()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm();
            for (int i = 0; i < 5; i++) Stroke(vm, i);
            var before = vm.HistoryItems.ToList();
            int changes = 0;
            vm.HistoryItems.CollectionChanged += (_, _) => changes++;
            Stroke(vm, 5);
            Assert.Equal(before.Count + 1, vm.HistoryItems.Count);
            for (int i = 0; i < before.Count; i++) Assert.Same(before[i], vm.HistoryItems[i]);
            Assert.Equal(1, changes);
            AssertInStep(vm);
        });
    }

    [Fact]
    // отмена и возврат - ни одной новой строки, меняются только пометки
    public void undo_and_redo_create_no_rows()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm();
            for (int i = 0; i < 6; i++) Stroke(vm, i);
            var before = vm.HistoryItems.ToList();
            int changes = 0;
            vm.HistoryItems.CollectionChanged += (_, _) => changes++;
            vm.UndoCommand.Execute(null);
            vm.UndoCommand.Execute(null);
            AssertInStep(vm);
            Assert.True(vm.HistoryItems[4].IsCurrent);
            Assert.True(vm.HistoryItems[6].IsFuture);
            vm.RedoCommand.Execute(null);
            AssertInStep(vm);
            vm.JumpToHistoryCommand.Execute(vm.HistoryItems[1]);
            AssertInStep(vm);
            Assert.Equal(0, changes);
            for (int i = 0; i < before.Count; i++) Assert.Same(before[i], vm.HistoryItems[i]);
        });
    }

    [Fact]
    // мазок после отмены отрезает будущее: лишние строки уходят, остальные те же
    public void a_stroke_after_undo_drops_the_future_rows()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm();
            for (int i = 0; i < 6; i++) Stroke(vm, i);
            var keep = vm.HistoryItems.Take(4).ToList();
            vm.UndoCommand.Execute(null);
            vm.UndoCommand.Execute(null);
            vm.UndoCommand.Execute(null);
            Stroke(vm, 99);
            AssertInStep(vm);
            Assert.Equal(5, vm.HistoryItems.Count);
            for (int i = 0; i < 4; i++) Assert.Same(keep[i], vm.HistoryItems[i]);
        });
    }

    [Fact]
    // выключили правку - галочка той же строки, без пересоздания
    public void toggling_an_entry_updates_its_row_in_place()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm();
            for (int i = 0; i < 4; i++) Stroke(vm, i);
            var row = vm.HistoryItems[2];
            vm.ToggleHistoryEntryCommand.Execute(row);
            Assert.Same(row, vm.HistoryItems[2]);
            Assert.False(row.Enabled);
            AssertInStep(vm);
            vm.ToggleHistoryEntryCommand.Execute(row);
            Assert.True(row.Enabled);
            AssertInStep(vm);
        });
    }

    [Fact]
    // отказ выключить правку - галочке напоминают правду, даже если строка не поменялась
    public void a_refused_toggle_reasserts_the_checkbox()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm();
            Stroke(vm, 0);
            vm.FlipHorizontalCommand.Execute(null);   // пишет картинку целиком
            var row = vm.HistoryItems[1];
            Assert.False(row.CanToggle);
            var heard = new List<string?>();
            row.PropertyChanged += (_, e) => heard.Add(e.PropertyName);
            vm.ToggleHistoryEntryCommand.Execute(row);
            Assert.True(row.Enabled);
            Assert.Contains(nameof(HistoryEntryViewModel.Enabled), heard);
        });
    }

    [Fact]
    // правка, пишущая картинку целиком, снимает выключатели ниже - у тех же строк
    public void a_snapshot_edit_removes_toggles_below_in_place()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm();
            for (int i = 0; i < 3; i++) Stroke(vm, i);
            var rows = vm.HistoryItems.ToList();
            Assert.True(rows[2].CanToggle);
            vm.FlipHorizontalCommand.Execute(null);
            for (int i = 0; i < rows.Count; i++) Assert.Same(rows[i], vm.HistoryItems[i]);
            Assert.False(rows[2].CanToggle);
            Assert.StartsWith("Выключить нельзя", rows[2].ToggleHint);
            AssertInStep(vm);
            vm.UndoCommand.Execute(null);
            Stroke(vm, 7);
            AssertInStep(vm);
            Assert.True(rows[2].CanToggle);
        });
    }

    [Fact]
    // лента на пределе глубины выбрасывает старое: строки старых уходят, остальные сдвигаются, не пересоздаются
    public void trimming_the_oldest_entries_shifts_rows_without_recreating_them()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm();
            vm.Document.History.MaxDepth = 10;
            for (int i = 0; i < 10; i++) Stroke(vm, i);
            var survivors = vm.HistoryItems.Skip(2).ToList();
            int changes = 0;
            vm.HistoryItems.CollectionChanged += (_, _) => changes++;
            Stroke(vm, 10);
            AssertInStep(vm);
            Assert.Equal("Дальше отмена не идёт", vm.HistoryItems[0].Label);
            for (int i = 0; i < survivors.Count; i++) Assert.Same(survivors[i], vm.HistoryItems[i + 1]);
            Assert.True(changes <= 2, $"{changes}");
        });
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 12).Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Seeds))]
    // случайная работа с лентой: строки после каждого шага - ровно то, что построила бы лента с нуля
    public void random_work_keeps_the_rows_in_step(int seed)
    {
        WpfRunner.Run(() =>
        {
            var rnd = new Random(seed);
            var vm = Vm();
            vm.Document.History.MaxDepth = 15;
            for (int step = 0; step < 120; step++)
            {
                int op = rnd.Next(10);
                if (op < 4) Stroke(vm, step);
                else if (op == 4) vm.UndoCommand.Execute(null);
                else if (op == 5) vm.RedoCommand.Execute(null);
                else if (op == 6 && vm.HistoryItems.Count > 1) vm.JumpToHistoryCommand.Execute(vm.HistoryItems[rnd.Next(vm.HistoryItems.Count)]);
                else if (op == 7 && vm.HistoryItems.Count > 1) vm.ToggleHistoryEntryCommand.Execute(vm.HistoryItems[1 + rnd.Next(vm.HistoryItems.Count - 1)]);
                else if (op == 8) vm.FlipHorizontalCommand.Execute(null);
                else vm.ActiveTool = rnd.Next(2) == 0 ? ToolKind.Eraser : ToolKind.Pencil;
                AssertInStep(vm);
            }
        });
    }

    // ───────── скорость ─────────

    [Fact]
    // на ленте в тысячу правок ещё один мазок правит ленту за миллисекунды
    public void a_stroke_on_a_long_history_updates_the_rows_quickly()
    {
        WpfRunner.Run(() =>
        {
            var vm = Vm();
            for (int i = 0; i < 1000; i++) Stroke(vm, i);
            Assert.Equal(1001, vm.HistoryItems.Count);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) Stroke(vm, 1000 + i);
            double perStroke = sw.Elapsed.TotalMilliseconds / 20;
            Assert.True(perStroke < 25, $"{perStroke:F1} мс на мазок");
            AssertInStep(vm);
        });
    }

    // Обход со стеком, а не рекурсивным yield: тот на глубоком дереве окна стоит O(узлов ×
    // глубина) и растягивал проверку до десятков секунд.
    private static List<DependencyObject> Tree(DependencyObject root)
    {
        var all = new List<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            all.Add(d);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        return all;
    }

    [Fact]
    // в окне создаются только видимые строки ленты, сколько бы правок ни было
    public void only_visible_history_rows_are_created()
    {
        WpfRunner.Run(() =>
        {
            var win = new MainWindow();
            var vm = (MainViewModel)win.DataContext;
            for (int i = 0; i < 300; i++) Stroke(vm, i);
            var content = (FrameworkElement)win.Content;
            win.Content = null;
            content.DataContext = vm;
            content.Measure(new Size(1600, 1000));
            content.Arrange(new Rect(0, 0, 1600, 1000));
            content.UpdateLayout();
            var tree = Tree(content);
            var list = tree.OfType<ItemsControl>().Single(c => ReferenceEquals(c.ItemsSource, vm.HistoryItems));
            Assert.Equal("HistoryList", list.Name);
            int realized = Tree(list).OfType<ContentPresenter>().Count(c => c.Content is HistoryEntryViewModel);
            Assert.InRange(realized, 1, 40);
            // Окно не закрываем: на изменённом документе Close спрашивает «Сохранить?»
            // настоящим окном и ждёт ответа, а заодно пишет место окна в настройки.
            // Как и в RenderSpeedTests, окно просто остаётся непоказанным.
        });
    }

    [Fact]
    // и в разметке: панель ленты виртуальная, полоса прокрутки внутри списка, а не снаружи
    public void the_history_list_is_virtualized_in_xaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(IOPath.Combine(dir.FullName, "PaintPro.sln"))) dir = dir.Parent;
        var xaml = File.ReadAllText(IOPath.Combine(dir!.FullName, "src", "PaintPro.Wpf", "MainWindow.xaml"));
        int at = xaml.IndexOf("ItemsSource=\"{Binding HistoryItems}\"");
        Assert.True(at > 0);
        int end = xaml.IndexOf("</ItemsControl>", at);
        var block = xaml[at..end];
        Assert.Contains("<VirtualizingStackPanel", block);
        Assert.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", block);
        Assert.Contains("<ItemsPresenter/>", block);
        // перед списком (в пределах панели) нет ScrollViewer, который обнимал бы его снаружи
        var before = xaml[Math.Max(0, at - 1200)..at];
        Assert.DoesNotContain("<ScrollViewer MaxHeight=\"220\"", before);
    }
}
