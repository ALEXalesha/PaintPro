using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Описание выпуска на GitHub (1.31.0).
///
/// До 1.31.0 описание собирал GitHub сам, и в выпусках Paint была одна ссылка «Full
/// Changelog» - что поменялось, понять было нельзя. Теперь короткое описание лежит в
/// docs/release-notes/&lt;тег&gt;.md, воркфлоу Release берёт его оттуда, а без файла выпуск
/// упал бы. Проверка ниже ловит это раньше - на обычном прогоне.
/// </summary>
public class ReleaseNotesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaintPro.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string CSharpVersion()
    {
        var v = typeof(PaintPro.ViewModels.MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        return v.Split('+')[0];
    }

    [Fact]
    // у нынешней версии есть короткое описание выпуска
    public void the_current_version_has_release_notes()
    {
        var file = Path.Combine(RepoRoot(), "docs", "release-notes", $"v{CSharpVersion()}.md");
        Assert.True(File.Exists(file), $"нет описания выпуска {file}");
        var text = File.ReadAllText(file);
        var bullets = Regex.Matches(text, @"^- ", RegexOptions.Multiline).Count;
        Assert.InRange(bullets, 2, 16);
        Assert.True(text.Length < 4000, $"описание выпуска длинное: {text.Length} знаков - оно должно быть коротким");
    }

    [Fact]
    // воркфлоу Release берёт описание из этого файла, а не собирает сам
    public void the_release_workflow_uses_the_notes_file()
    {
        var yml = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml"));
        Assert.Contains("body_path: docs/release-notes/${{ github.ref_name }}.md", yml);
        Assert.DoesNotContain("generate_release_notes: true", yml);
    }
}
