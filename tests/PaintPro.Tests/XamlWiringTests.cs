using System.IO;
using System.Text.RegularExpressions;
using PaintPro.Models;
using PaintPro.ViewModels;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Проводка XAML: привязки, хоткеи и подсказки к ним.
///
/// Опечатка в пути привязки или в имени команды не ломает сборку - она молча даёт пустую
/// привязку и мёртвую кнопку, и увидеть это можно только запустив приложение. Прежде такая
/// проверка была, но списком, выписанным руками
/// (<see cref="BindingPathTests"/>), а список отстаёт от разметки. Здесь разметка читается
/// целиком, так что новая привязка проверяется сама собой.
/// </summary>
public class XamlWiringTests
{
    /// <summary>Корень репозитория: поднимаемся от сборки, пока не увидим PaintPro.sln.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaintPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string MainWindowXaml()
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "PaintPro.Wpf", "MainWindow.xaml"));

    /// <summary>
    /// Каждый путь привязки из MainWindow.xaml обязан существовать хоть на одном из типов,
    /// которые бывают его DataContext. Опечатка в пути молча даёт пустую привязку -
    /// увидеть это можно только запустив приложение.
    /// </summary>
    [Fact]
    public void every_binding_path_resolves()
    {
        var xaml = MainWindowXaml();
        var types = new[]
        {
            typeof(MainViewModel), typeof(LayerListItemViewModel),
            typeof(HistoryEntryViewModel), typeof(ColorEntryViewModel),
            typeof(Document), typeof(Layer), typeof(PixelLayer),
        };

        var dead = new List<string>();
        foreach (Match m in Regex.Matches(xaml, @"\{Binding\s+(?:Path=)?([A-Za-z_][A-Za-z0-9_.]*)"))
        {
            var path = m.Groups[1].Value;
            if (path is "RelativeSource" or "ElementName" or "Source") continue;
            // {Binding DataContext.XxxCommand, RelativeSource=...} - обычный способ
            // дотянуться из шаблона до вьюмодели: DataContext живёт на элементе, а не на ней.
            if (path.StartsWith("DataContext.")) path = path["DataContext.".Length..];
            var head = path.Split('.')[0];
            bool found = types.Any(t =>
                System.ComponentModel.TypeDescriptor.GetProperties(t)[head] is not null);
            if (!found) dead.Add(path);
        }
        Assert.True(dead.Count == 0, "привязки в никуда: " + string.Join(", ", dead.Distinct()));
    }

    /// <summary>Каждый хоткей инструмента называет существующий инструмент.</summary>
    [Fact]
    public void every_tool_hotkey_names_a_real_tool()
    {
        var xaml = MainWindowXaml();
        var bad = new List<string>();
        foreach (Match m in Regex.Matches(xaml,
            @"SelectToolCommand\}""\s+CommandParameter=""([^""]+)"""))
        {
            if (!Enum.TryParse<ToolKind>(m.Groups[1].Value, out _)) bad.Add(m.Groups[1].Value);
        }
        Assert.True(bad.Count == 0, "хоткей в никуда: " + string.Join(", ", bad));
    }

    /// <summary>Каждый инструмент, у которого есть кнопка, доступен вьюмодели.</summary>
    [Fact]
    public void every_tool_kind_has_an_instance()
    {
        var vm = new MainViewModel();
        foreach (ToolKind kind in Enum.GetValues<ToolKind>())
        {
            vm.ActiveTool = kind;
            Assert.NotNull(vm.ActiveToolInstance);
            Assert.Equal(kind, vm.ActiveTool);
        }
    }

    /// <summary>
    /// У каждого инструмента, которому в XAML обещан хоткей в подсказке, этот хоткей
    /// действительно назначен. Подсказка «Кисть (B)» без KeyBinding на B - обещание,
    /// которого программа не держит.
    /// </summary>
    [Fact]
    public void tooltip_hotkeys_are_really_bound()
    {
        var xaml = MainWindowXaml();
        var bound = new HashSet<string>();
        foreach (Match m in Regex.Matches(xaml, @"<KeyBinding\s+Key=""([A-Za-z])""\s+Command"))
            bound.Add(m.Groups[1].Value.ToUpperInvariant());

        var promised = new List<string>();
        foreach (Match m in Regex.Matches(xaml, @"ToolTip=""[^""]*\(([A-Za-z])\)"""))
            promised.Add(m.Groups[1].Value.ToUpperInvariant());

        var broken = promised.Where(k => !bound.Contains(k)).Distinct().ToList();
        Assert.True(broken.Count == 0, "обещаны в подсказке, но не назначены: " + string.Join(", ", broken));
    }

    /// <summary>Каждая команда, на которую ссылается XAML, существует на вьюмодели.</summary>
    [Fact]
    public void every_bound_command_exists()
    {
        var xaml = MainWindowXaml();
        var props = System.ComponentModel.TypeDescriptor.GetProperties(typeof(MainViewModel));
        var dead = new List<string>();
        foreach (Match m in Regex.Matches(xaml, @"Command=""\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)\}"""))
        {
            var name = m.Groups[1].Value;
            if (props[name] is null) dead.Add(name);
        }
        Assert.True(dead.Count == 0, "команды в никуда: " + string.Join(", ", dead.Distinct()));
    }
}
