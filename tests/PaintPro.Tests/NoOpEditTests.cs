using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Tools;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Действие, ничего не изменившее, не должно попадать в историю. Пустая запись —
/// не только мусор в списке: признак несохранённой работы считается по позиции
/// курсора, и приложение начинает спрашивать про сохранение после ничего.
/// </summary>
public class NoOpEditTests
{
    private static (Document doc, ToolContext ctx) MakeDoc()
    {
        var doc = new Document(20, 20);
        return (doc, new ToolContext(doc) { PrimaryColor = SKColors.Black, Opacity = 1f });
    }

    [Fact]
    public void Filling_an_area_already_that_colour_records_nothing()
    {
        var (doc, ctx) = MakeDoc();
        ctx.PrimaryColor = SKColors.White; // холст и так белый
        var before = doc.History.Cursor;

        new FillTool().OnPointerDown(new SKPoint(10, 10), ctx);

        Assert.Equal(before, doc.History.Cursor);
    }

    [Fact]
    public void A_fill_that_changes_pixels_is_still_recorded()
    {
        var (doc, ctx) = MakeDoc();
        ctx.PrimaryColor = SKColors.Red;
        var before = doc.History.Cursor;

        new FillTool().OnPointerDown(new SKPoint(10, 10), ctx);

        Assert.Equal(before + 1, doc.History.Cursor);
        Assert.Equal(SKColors.Red, ((PixelLayer)doc.Layers[0]).Bitmap.GetPixel(10, 10));
    }

    [Fact]
    public void A_recorded_fill_still_undoes_cleanly()
    {
        var (doc, ctx) = MakeDoc();
        ctx.PrimaryColor = SKColors.Red;
        new FillTool().OnPointerDown(new SKPoint(10, 10), ctx);

        doc.History.Undo(doc);

        Assert.Equal(SKColors.White, ((PixelLayer)doc.Layers[0]).Bitmap.GetPixel(10, 10));
    }

    [Fact]
    public void A_click_outside_the_canvas_records_nothing()
    {
        var (doc, ctx) = MakeDoc();
        ctx.PrimaryColor = SKColors.Red;
        var before = doc.History.Cursor;

        new FillTool().OnPointerDown(new SKPoint(500, 500), ctx);

        Assert.Equal(before, doc.History.Cursor);
    }
}

/// <summary>
/// Версия в интерфейсе должна приходить из сборки, а не из строкового литерала:
/// «Paint Pro 1.0» в статусбаре и в «О программе» отстало на шесть релизов, потому
/// что было вписано руками в двух местах.
/// </summary>
public class VersionLabelTests
{
    [Fact]
    public void Version_comes_from_the_assembly()
    {
        var expected = typeof(PaintPro.ViewModels.MainViewModel).Assembly.GetName().Version!;
        Assert.Equal($"{expected.Major}.{expected.Minor}.{expected.Build}",
                     PaintPro.ViewModels.MainViewModel.AppVersion);
    }

    [Fact]
    public void Version_is_not_the_old_hardcoded_one()
    {
        Assert.NotEqual("1.0.0", PaintPro.ViewModels.MainViewModel.AppVersion);
    }
}

/// <summary>
/// Привязки XAML резолвятся через TypeDescriptor по экземпляру DataContext, и
/// статические свойства так не находятся: привязка молча остаётся пустой, а увидеть
/// это можно только запустив приложение. Проверяем тем же способом, каким это делает
/// WPF, чтобы не полагаться на глаз.
/// </summary>
public class BindingPathTests
{
    [Theory]
    [InlineData("VersionLabel")]
    [InlineData("Palette")]
    [InlineData("RecentColors")]
    [InlineData("LayerItems")]
    [InlineData("HistoryItems")]
    [InlineData("PrimaryColorBrush")]
    [InlineData("ToolSize")]
    [InlineData("Opacity")]
    [InlineData("Zoom")]
    [InlineData("ShapeFill")]
    [InlineData("ActiveTool")]
    [InlineData("Document")]
    public void MainViewModel_exposes_the_path_XAML_binds_to(string path)
    {
        var props = System.ComponentModel.TypeDescriptor.GetProperties(
            typeof(PaintPro.ViewModels.MainViewModel));
        Assert.NotNull(props[path]);
    }
}
