using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PaintPro.Services;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Темы оформления.
///
/// Проверять тут надо не «красиво ли», а три правила, нарушение которых делает приложение
/// неработоспособным:
///
///   1. у всех тем ОДИН И ТОТ ЖЕ набор ключей — иначе на смене какая-нибудь кисть окажется
///      пустой и отрисовка упадёт;
///   2. текст обязан быть виден на фоне СВОЕЙ темы;
///   3. подложка того, что всплывает над ХОЛСТОМ, обязана оставаться тёмной в ЛЮБОЙ теме:
///      бумага белая всегда. Это уже стоило одной находки в Electron-версии.
/// </summary>
public class ThemeTests
{
    private static readonly string[] Ids = { "Glass", "Formal", "Light", "Night", "Warm" };

    private static ResourceDictionary Load(string id) => WpfRunner.Invoke(() =>
        new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PaintPro;component/Resources/Themes/{id}.xaml")
        });

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color ColorOf(ResourceDictionary d, string key)
    {
        var brush = d[key];
        return brush switch
        {
            SolidColorBrush s => s.Color,
            GradientBrush g => g.GradientStops[0].Color,
            _ => throw new InvalidOperationException($"{key}: не кисть"),
        };
    }

    [Fact]
    public void Тем_ровно_пять_и_порядок_задан()
    {
        Assert.Equal(Ids, ThemeService.All.Select(t => t.Id).ToArray());
        Assert.All(ThemeService.All, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Note));
        });
    }

    [Fact]
    public void У_каждой_темы_есть_свой_файл()
    {
        foreach (var id in Ids) Assert.NotNull(Load(id));
    }

    [Fact]
    public void Набор_ключей_у_всех_тем_одинаковый()
    {
        // Разойдись он хоть на один ключ - на смене темы кисть окажется пустой, и падать
        // будет не здесь, а в отрисовке, где причину уже не увидеть.
        var reference = Load("Glass").Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(x => x).ToArray();
        foreach (var id in Ids.Skip(1))
        {
            var keys = Load(id).Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(x => x).ToArray();
            Assert.Equal(reference, keys);
        }
    }

    [Fact]
    public void В_каждой_теме_текст_виден_на_её_фоне()
    {
        var bad = new List<string>();
        foreach (var id in Ids)
        {
            var d = Load(id);
            var diff = Math.Abs(Luminance(ColorOf(d, "TextPrimary")) - Luminance(ColorOf(d, "AppBase")));
            if (diff < 0.4) bad.Add($"{id}: разница {diff:F2}");
        }
        Assert.True(bad.Count == 0, "текст сливается с фоном — " + string.Join("; ", bad));
    }

    [Fact]
    public void Приглушённый_текст_тоже_виден()
    {
        var bad = new List<string>();
        foreach (var id in Ids)
        {
            var d = Load(id);
            var diff = Math.Abs(Luminance(ColorOf(d, "TextDim")) - Luminance(ColorOf(d, "AppBase")));
            if (diff < 0.2) bad.Add($"{id}: разница {diff:F2}");
        }
        Assert.True(bad.Count == 0, "приглушённый текст сливается — " + string.Join("; ", bad));
    }

    [Fact]
    public void Подложка_над_холстом_тёмная_в_любой_теме()
    {
        // Холст белый всегда. Светлая подложка над ним со светлым текстом - это ровно тот
        // дефект, что был у выпадающего меню в 1.23.0.
        var bad = new List<string>();
        foreach (var id in Ids)
        {
            var d = Load(id);
            var bg = Luminance(ColorOf(d, "OverlayBg"));
            var fg = Luminance(ColorOf(d, "OverlayText"));
            if (bg > 0.35 || Math.Abs(bg - fg) < 0.4) bad.Add($"{id}: фон {bg:F2}, текст {fg:F2}");
        }
        Assert.True(bad.Count == 0, "подложка над холстом посветлела — " + string.Join("; ", bad));
    }

    [Fact]
    public void Фон_выпадающего_меню_тёмный_в_любой_теме()
    {
        // Выпадающий список тоже всплывает и тоже может лечь на холст.
        var bad = new List<string>();
        foreach (var id in Ids)
        {
            var lum = Luminance(ColorOf(Load(id), "MenuBg"));
            if (lum > 0.35) bad.Add($"{id}: {lum:F2}");
        }
        Assert.True(bad.Count == 0, "фон меню посветлел — " + string.Join("; ", bad));
    }

    [Fact]
    public void Темы_отличаются_друг_от_друга()
    {
        var seen = Ids.Select(id =>
        {
            var d = Load(id);
            return $"{ColorOf(d, "AppBase")}|{ColorOf(d, "TextPrimary")}|{ColorOf(d, "Accent")}";
        }).ToArray();
        Assert.Equal(Ids.Length, seen.Distinct().Count());
    }

    [Fact]
    public void Неизвестное_имя_откатывается_к_исходной()
    {
        // Файл настроек могли поправить руками. Приложение без оформления - не приложение.
        Assert.False(ThemeService.Exists("такой-нет"));
        Assert.Null(ThemeService.Find("такой-нет"));
        Assert.Equal(ThemeService.DefaultId, WpfRunner.Invoke(() => ThemeService.Apply("такой-нет")));
    }

    [Fact]
    public void Смена_темы_подменяет_первый_словарь_а_не_добавляет_новый()
    {
        WpfRunner.Invoke(() =>
        {
            var app = Application.Current!;
            var before = app.Resources.MergedDictionaries.Count;
            ThemeService.Apply("Night");
            ThemeService.Apply("Warm");
            ThemeService.Apply("Glass");
            Assert.Equal(before, app.Resources.MergedDictionaries.Count);
            return 0;
        });
    }

    [Fact]
    public void После_смены_темы_ключи_разрешаются()
    {
        // Проверка того, ради чего вся затея: после подмены словаря приложение обязано
        // находить каждую кисть. Пустая кисть валит отрисовку, а не тест.
        var keys = new[] { "Accent", "TextPrimary", "TextDim", "MenuBg", "OverlayBg", "OverlayText",
                           "AppBase", "GlassBg", "GlassBgStrong", "GlassBorder", "WindowBg",
                           "Blob1", "Blob2", "Blob3", "Blob4" };
        WpfRunner.Invoke(() =>
        {
            foreach (var id in Ids)
            {
                ThemeService.Apply(id);
                foreach (var key in keys)
                {
                    Assert.True(Application.Current!.TryFindResource(key) is Brush,
                        $"{id}: ключ {key} не разрешился");
                }
            }
            ThemeService.Apply(ThemeService.DefaultId);
            return 0;
        });
    }

    [Fact]
    public void Разметка_обращается_к_теме_динамически()
    {
        // StaticResource разрешается один раз при загрузке и подмены словаря не заметит:
        // тема сменится, а окно останется прежним. Проверяем сам исходник разметки - это
        // единственное место, где такую ошибку видно до запуска.
        //
        // Исключение ровно одно: Converter у привязки. Это НЕ свойство зависимости, и
        // DynamicResource там запрещён - приложение падает прямо при запуске. Найдено
        // именно так: тесты проходили, а окно не открывалось.
        //
        // Смотрим ВСЮ разметку, кроме самих тем. Прежде список был из двух файлов, и мимо
        // него прошёл стиль иконок в ToolIcons.xaml: половина кнопок инструментов
        // оставалась белой в светлой теме. Нашлось на кадре для README.
        var root = Path.Combine(RepoRoot(), "src", "PaintPro.Wpf");
        var files = Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("Resources", "Themes")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => Path.GetRelativePath(root, f))
            .ToArray();
        Assert.Contains(Path.Combine("Resources", "ToolIcons.xaml"), files);
        foreach (var file in files)
        {
            var path = Path.Combine(root, file);
            var bad = File.ReadAllLines(path)
                .Select((line, i) => (line, no: i + 1))
                .Where(x => x.line.Contains("StaticResource")
                            && !x.line.Contains("Converter={StaticResource")
                            && !x.line.Contains("ConverterParameter={StaticResource"))
                .Select(x => $"{file}:{x.no}")
                .ToArray();
            Assert.True(bad.Length == 0,
                "остался StaticResource — тема не подхватится: " + string.Join(", ", bad));
        }
    }

    [Fact]
    public void Converter_остаётся_статическим()
    {
        // Обратная сторона предыдущей проверки: DynamicResource в Converter валит запуск.
        var path = Path.Combine(RepoRoot(), "src", "PaintPro.Wpf", "MainWindow.xaml");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("Converter={DynamicResource", text);
        Assert.DoesNotContain("ConverterParameter={DynamicResource", text);
    }

    [Fact]
    public void У_каждой_роли_пункта_меню_есть_свой_шаблон()
    {
        // Ролей у MenuItem три. У той, что осталась без шаблона, всплывающее окно рисует
        // система - светлой панелью, на которой светлый текст пропадает начисто. Так и
        // вышло с подменю «Вид → Тема»: первым вложенным списком в программе.
        var path = Path.Combine(RepoRoot(), "src", "PaintPro.Wpf", "Resources", "GlassStyles.xaml");
        var text = File.ReadAllText(path);
        foreach (var role in new[] { "TopLevelHeader", "SubmenuItem", "SubmenuHeader" })
        {
            Assert.Contains($"Property=\"Role\" Value=\"{role}\"", text);
        }
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
