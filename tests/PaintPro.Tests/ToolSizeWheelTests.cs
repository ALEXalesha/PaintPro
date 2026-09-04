using System.IO;
using PaintPro.Models;
using PaintPro.ViewModels;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Размер инструмента колесом мыши.
///
/// Место занятое: с Ctrl колесо уже меняло масштаб, а без Ctrl прокручивался
/// ScrollViewer - и обязан прокручиваться дальше, иначе увеличенный холст окажется
/// заперт. Отсюда правило: размер меняется, только пока курсор над САМИМ холстом
/// (это решает CanvasView) и только у инструмента, у которого размер есть.
///
/// Здесь проверяется вторая половина - решение о размере, которое принимает ViewModel.
/// </summary>
public class ToolSizeWheelTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaintPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Колесо_вверх_увеличивает_а_вниз_уменьшает()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel { ActiveTool = ToolKind.Pencil, ToolSize = 20 };

            Assert.True(vm.AdjustToolSize(up: true));
            Assert.True(vm.ToolSize > 20);

            var grown = vm.ToolSize;
            Assert.True(vm.AdjustToolSize(up: false));
            Assert.True(vm.ToolSize < grown);
        });
    }

    [Fact]
    public void Шаг_растёт_вместе_с_размером()
    {
        // Ровно один пиксель за засечку означал бы сотню засечек на весь ползунок.
        Assert.Equal(1, MainViewModel.ToolSizeStep(1));
        Assert.Equal(1, MainViewModel.ToolSizeStep(4));
        Assert.True(MainViewModel.ToolSizeStep(60) > MainViewModel.ToolSizeStep(4));
        Assert.Equal(10, MainViewModel.ToolSizeStep(100));
    }

    [Fact]
    public void Шаг_никогда_не_ноль()
    {
        // Нулевой шаг - это колесо, которое крутится и не делает ничего.
        for (int s = MainViewModel.MinToolSize; s <= MainViewModel.MaxToolSize; s++)
            Assert.True(MainViewModel.ToolSizeStep(s) >= 1, $"шаг ноль при размере {s}");
    }

    [Fact]
    public void У_инструментов_без_размера_колесо_ничего_не_делает()
    {
        var without = new[] { ToolKind.Fill, ToolKind.Picker, ToolKind.Select,
                              ToolKind.Quad, ToolKind.Crop, ToolKind.Hand };
        foreach (var t in without) Assert.False(MainViewModel.HasToolSize(t), t.ToString());

        var with = new[] { ToolKind.Pencil, ToolKind.Brush, ToolKind.Marker, ToolKind.Eraser,
                           ToolKind.Line, ToolKind.Rect, ToolKind.Ellipse, ToolKind.Triangle,
                           ToolKind.Star, ToolKind.Arrow, ToolKind.Heart, ToolKind.Text };
        foreach (var t in with) Assert.True(MainViewModel.HasToolSize(t), t.ToString());

        // Список обязан покрывать ВЕСЬ enum: новый инструмент не должен молча попасть
        // в «есть размер» просто потому, что его забыли вписать.
        Assert.Equal(Enum.GetValues<ToolKind>().Length, without.Length + with.Length);
    }

    [Fact]
    public void У_заливки_колесо_не_трогает_размер()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel { ActiveTool = ToolKind.Fill, ToolSize = 12 };
            Assert.False(vm.AdjustToolSize(up: true));
            Assert.Equal(12, vm.ToolSize);
        });
    }

    [Fact]
    public void На_пределе_колесо_говорит_словами()
    {
        // Молчаливый отказ читается как поломка приложения.
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel { ActiveTool = ToolKind.Brush, ToolSize = MainViewModel.MaxToolSize };
            Assert.False(vm.AdjustToolSize(up: true));
            Assert.Contains("предел", vm.StatusHint);
            Assert.Equal(MainViewModel.MaxToolSize, vm.ToolSize);

            vm.StatusHint = "";
            vm.ToolSize = MainViewModel.MinToolSize;
            Assert.False(vm.AdjustToolSize(up: false));
            Assert.Contains("предел", vm.StatusHint);
            Assert.Equal(MainViewModel.MinToolSize, vm.ToolSize);
        });
    }

    [Fact]
    public void Размер_не_выходит_за_границы_ни_при_какой_накрутке()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel { ActiveTool = ToolKind.Pencil, ToolSize = 50 };
            for (int i = 0; i < 200; i++) vm.AdjustToolSize(up: true);
            Assert.Equal(MainViewModel.MaxToolSize, vm.ToolSize);
            for (int i = 0; i < 200; i++) vm.AdjustToolSize(up: false);
            Assert.Equal(MainViewModel.MinToolSize, vm.ToolSize);
        });
    }

    [Fact]
    public void Смена_размера_не_пишет_в_историю()
    {
        // Размер - это настройка, а не правка рисунка. Запись в ленту сделала бы отмену
        // бесполезной: каждый поворот колеса съедал бы шаг назад.
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel { ActiveTool = ToolKind.Pencil, ToolSize = 20 };
            var before = vm.Document.History.Commands.Count;
            vm.AdjustToolSize(up: true);
            vm.AdjustToolSize(up: false);
            Assert.Equal(before, vm.Document.History.Commands.Count);
        });
    }

    [Fact]
    public void Признак_наличия_размера_обновляется_при_смене_инструмента()
    {
        // Без уведомления привязка застыла бы на прежнем инструменте, и колесо над
        // холстом слушалось бы того, кого уже отложили.
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel { ActiveTool = ToolKind.Pencil };
            var fired = 0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.ActiveToolHasSize)) fired++;
            };

            vm.ActiveTool = ToolKind.Fill;
            Assert.False(vm.ActiveToolHasSize);
            vm.ActiveTool = ToolKind.Eraser;
            Assert.True(vm.ActiveToolHasSize);
            Assert.Equal(2, fired);
        });
    }

    [Fact]
    public void Ползунок_берёт_границы_из_ViewModel_а_не_из_своих_чисел()
    {
        // Разойдись они - колесо и ползунок упирались бы в разные пределы, и на ползунке
        // остались бы значения, которых колесо достичь не может.
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PaintPro.Wpf", "MainWindow.xaml"));
        var i = xaml.IndexOf("Value=\"{Binding ToolSize}\"", StringComparison.Ordinal);
        Assert.True(i > 0, "ползунок размера в разметке не найден");
        var slider = xaml[Math.Max(0, i - 400)..i];
        Assert.Contains("Minimum=\"{Binding ToolSizeMin", slider);
        Assert.Contains("Maximum=\"{Binding ToolSizeMax", slider);
    }

    [Fact]
    public void Колесо_и_ползунок_упираются_в_одно_и_то_же()
    {
        WpfRunner.Run(() =>
        {
            var vm = new MainViewModel();
            Assert.Equal(MainViewModel.MinToolSize, vm.ToolSizeMin);
            Assert.Equal(MainViewModel.MaxToolSize, vm.ToolSizeMax);
            Assert.True(vm.ToolSizeMin < vm.ToolSizeMax);
        });
    }
}
