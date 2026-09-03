using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace PaintPro.Services;

/// <summary>Одна тема оформления: имя для меню и файл словаря.</summary>
public sealed record ThemeInfo(string Id, string Name, string Note);

/// <summary>
/// Темы оформления.
///
/// Тема — это НАБОР ТЕХ ЖЕ КЛЮЧЕЙ ресурсов (Resources/Themes/*.xaml). Всё оформление ходит
/// через них, поэтому смена сводится к подмене одного словаря в App.Resources. Работает это
/// только потому, что разметка обращается к ресурсам через DynamicResource: StaticResource
/// разрешается один раз при загрузке и подмены не заметит.
///
/// Словарь темы стоит ПЕРВЫМ в MergedDictionaries, и подмена идёт по индексу 0. Порядок
/// важен: стили из GlassStyles ссылаются на ключи темы, и если тема встанет после них,
/// ссылки будут разрешаться в пустоту.
/// </summary>
public static class ThemeService
{
    /// <summary>Порядок здесь — это порядок в меню.</summary>
    public static readonly IReadOnlyList<ThemeInfo> All = new[]
    {
        new ThemeInfo("Glass",  "Стеклянная", "Как было: цветной градиент и стекло"),
        new ThemeInfo("Formal", "Строгая",    "Ровный тёмный фон, приглушённый синий"),
        new ThemeInfo("Light",  "Светлая",    "Тёмный текст на светлом"),
        new ThemeInfo("Night",  "Ночная",     "Почти чёрный фон для тёмной комнаты"),
        new ThemeInfo("Warm",   "Тёплая",     "Охра и кофе вместо синевы"),
    };

    public const string DefaultId = "Glass";

    private static string _current = DefaultId;

    /// <summary>Тема, стоящая сейчас.</summary>
    public static string Current => _current;

    /// <summary>Есть ли такая тема. Имя могли принести из файла настроек, правленного руками.</summary>
    public static bool Exists(string? id) => All.Any(t => t.Id == id);

    /// <summary>Имя темы или <c>null</c>, если такой нет.</summary>
    public static ThemeInfo? Find(string? id) => All.FirstOrDefault(t => t.Id == id);

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PaintPro", "theme.txt");

    /// <summary>
    /// Поставить тему. Неизвестное имя откатывает к исходной: файл настроек могли поправить
    /// руками, а приложение без оформления — это не приложение.
    /// </summary>
    public static string Apply(string? id)
    {
        var theme = Find(id) ?? Find(DefaultId)!;
        var app = Application.Current;
        if (app is not null)
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/PaintPro;component/Resources/Themes/{theme.Id}.xaml")
            };
            var merged = app.Resources.MergedDictionaries;
            if (merged.Count == 0) merged.Add(dict);
            else merged[0] = dict;
        }
        _current = theme.Id;
        return theme.Id;
    }

    /// <summary>Запомнить выбор. Не смогли записать — не беда: тема вернётся к исходной.</summary>
    public static void Save(string id)
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, id);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Прочитать запомненный выбор. Нет файла или мусор внутри — исходная тема.</summary>
    public static string Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return DefaultId;
            var id = File.ReadAllText(path).Trim();
            return Exists(id) ? id : DefaultId;
        }
        catch (IOException) { return DefaultId; }
        catch (UnauthorizedAccessException) { return DefaultId; }
    }

    /// <summary>Применить запомненную тему при запуске.</summary>
    public static string ApplySaved() => Apply(Load());
}
