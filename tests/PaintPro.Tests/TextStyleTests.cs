using PaintPro.Models;
using PaintPro.Tools;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Оформление текста (1.28.0): шрифт, размер, жирный, курсив, подчёркнутый - как в панели
/// текста Electron-версии. Раньше размер брался у ползунка кисти, а начертаний не было.
/// </summary>
public class TextStyleTests
{
    private static PixelLayer Paper(Document d) => (PixelLayer)d.Layers[0];

    /// <summary>Сколько непустых (не белых) точек и где самая нижняя.</summary>
    private static (int count, int bottom) Ink(Document d)
    {
        var bmp = Paper(d).Bitmap;
        int count = 0, bottom = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Alpha > 0 && (c.Red < 200 || c.Green < 200 || c.Blue < 200)) { count++; bottom = y; }
            }
        return (count, bottom);
    }

    private static Document Write(string text, TextStyle style)
    {
        var doc = new Document(400, 160);
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Black, Opacity = 1f };
        var t = new TextTool();
        t.OnPointerDown(new SKPoint(20, 80), ctx);
        t.CommitText(text, style);
        return doc;
    }

    [Theory]
    [InlineData("24", 24)]
    [InlineData(" 40 ", 40)]
    [InlineData("3", 8)]
    [InlineData("999", 200)]
    [InlineData("abc", 8)]
    [InlineData("", 8)]
    [InlineData(null, 8)]
    public void The_size_field_is_read_like_the_electron_one(string? raw, float expected) =>
        Assert.Equal(expected, TextStyle.ClampSize(raw));

    [Fact]
    public void The_size_from_the_dialog_is_the_size_on_the_canvas()
    {
        var small = Ink(Write("Hello", TextStyle.Default with { Size = 16 })).count;
        var large = Ink(Write("Hello", TextStyle.Default with { Size = 48 })).count;
        Assert.True(large > small * 4, $"{small} -> {large}");
    }

    [Fact]
    public void Bold_puts_down_more_ink_than_normal()
    {
        var normal = Ink(Write("Hello", TextStyle.Default)).count;
        var bold = Ink(Write("Hello", TextStyle.Default with { Bold = true })).count;
        Assert.True(bold > normal * 1.15, $"{normal} -> {bold}");
    }

    [Fact]
    public void Underline_draws_a_line_below_the_letters()
    {
        // «a» без выносных элементов: всё, что ниже базовой линии, - подчёркивание.
        var plain = Ink(Write("aaaa", TextStyle.Default));
        var under = Ink(Write("aaaa", TextStyle.Default with { Underline = true }));
        Assert.True(under.bottom > plain.bottom, $"{plain.bottom} -> {under.bottom}");
        Assert.True(under.count > plain.count);
    }

    [Fact]
    public void Italic_is_not_the_same_picture_as_upright()
    {
        var upright = Paper(Write("Hello", TextStyle.Default)).Bitmap.Bytes;
        var italic = Paper(Write("Hello", TextStyle.Default with { Italic = true })).Bitmap.Bytes;
        Assert.NotEqual(upright, italic);
    }

    [Fact]
    public void Every_font_of_the_list_writes_something()
    {
        foreach (var (_, family) in Views.TextDialog.Fonts)
            Assert.True(Ink(Write("Ab", TextStyle.Default with { Family = family })).count > 0, family);
    }
}
