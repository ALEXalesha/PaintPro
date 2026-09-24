using PaintPro.Services;
using SkiaSharp;
using Xunit;
using static PaintPro.Services.ColorMath;

namespace PaintPro.Tests;

/// <summary>Окно выбора цвета (1.28.0): арифметика RGB ↔ HSV ↔ HEX.</summary>
public class ColorMathTests
{
    [Theory]
    [InlineData(255, 0, 0, 0, 1, 1)]
    [InlineData(0, 255, 0, 120, 1, 1)]
    [InlineData(0, 0, 255, 240, 1, 1)]
    [InlineData(255, 255, 255, 0, 0, 1)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(128, 128, 128, 0, 0, 128 / 255.0)]
    public void Known_colours_have_known_hue_saturation_and_value(byte r, byte g, byte b, double h, double s, double v)
    {
        var hsv = ToHsv(new SKColor(r, g, b));
        Assert.Equal(h, hsv.H, 6);
        Assert.Equal(s, hsv.S, 6);
        Assert.Equal(v, hsv.V, 6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Every_colour_survives_the_trip_through_hsv(int seed)
    {
        // Иначе окно, открытое на текущем цвете, отдало бы по OK чуть другой цвет.
        var rnd = new Random(seed * 31337);
        for (var i = 0; i < 20000; i++)
        {
            var c = new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
            Assert.Equal(c, FromHsv(ToHsv(c)));
        }
    }

    [Fact]
    public void Any_position_in_the_square_and_on_the_hue_bar_gives_a_colour()
    {
        var rnd = new Random(7);
        for (var i = 0; i < 20000; i++)
        {
            // Мышь уводят за край квадрата - числа выходят за 0..1 и за 0..360.
            var hsv = new Hsv(rnd.NextDouble() * 1000 - 500, rnd.NextDouble() * 3 - 1, rnd.NextDouble() * 3 - 1);
            var c = FromHsv(hsv);
            Assert.Equal(255, c.Alpha);
        }
        Assert.Equal(FromHsv(new Hsv(0, 1, 1)), FromHsv(new Hsv(360, 1, 1)));
    }

    [Theory]
    [InlineData("#5B8DEF", 0x5B, 0x8D, 0xEF)]
    [InlineData("5b8def", 0x5B, 0x8D, 0xEF)]
    [InlineData("  #abc ", 0xAA, 0xBB, 0xCC)]
    [InlineData("#000000", 0, 0, 0)]
    public void Hex_is_read_as_people_type_it(string text, byte r, byte g, byte b) =>
        Assert.Equal(new SKColor(r, g, b), ParseHex(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    [InlineData("#1234567")]
    public void Anything_else_is_not_a_colour(string? text) => Assert.Null(ParseHex(text));

    [Fact]
    public void Hex_written_is_hex_read()
    {
        var rnd = new Random(11);
        for (var i = 0; i < 5000; i++)
        {
            var c = new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
            Assert.Equal(c, ParseHex(ToHex(c)));
        }
    }
}
