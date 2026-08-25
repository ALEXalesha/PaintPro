using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Копирование многоугольного выделения и размер текста: две ручки, которые делали не то,
/// что показывали.
/// </summary>
public class PolygonCopyAndTextSizeTests
{
    private static PixelLayer Paper(Document d) => (PixelLayer)d.Layers[0];

    private static void Dot(PixelLayer l, int x, int y, SKColor c)
    {
        using var canvas = new SKCanvas(l.Bitmap);
        using var p = new SKPaint { Color = c, BlendMode = SKBlendMode.Src };
        canvas.DrawRect(new SKRect(x, y, x + 1, y + 1), p);
    }

    private static int NonWhite(PixelLayer l)
    {
        int n = 0;
        for (int y = 0; y < l.Height; y++)
            for (int x = 0; x < l.Width; x++)
                if (l.Bitmap.GetPixel(x, y) != SKColors.White) n++;
        return n;
    }

    /// <summary>
    /// Копия многоугольного выделения повторяет его форму, а не описанный вокруг
    /// прямоугольник.
    ///
    /// Углы габарита лежат ВНЕ выделенного. Пока копирование шло по нему, Ctrl+C по
    /// треугольнику клал в буфер прямоугольную заплатку с чужими пикселями по углам, и
    /// вставка возвращала на холст не фигуру, а кусок фона с фигурой внутри. Delete по
    /// тому же выделению при этом стирал именно многоугольник - копия и вырезание
    /// описывали разные области. Поднятый многоугольник маску уважал с самого начала
    /// (<see cref="Document.DrawPickup"/>), рамка - нет.
    /// </summary>
    [Fact]
    public void Copying_a_polygon_selection_keeps_its_shape()
    {
        var doc = new Document(60, 40);
        Dot(Paper(doc), 1, 1, SKColors.Red);      // угол габарита, вне многоугольника
        Dot(Paper(doc), 20, 20, SKColors.Blue);   // внутри
        doc.Selection = new PolygonSelection(
            new SKPoint(20, 0), new SKPoint(40, 30), new SKPoint(0, 30), new SKPoint(0, 30));

        using var copy = ClipboardService.ExtractForClipboard(doc);

        Assert.NotNull(copy);
        Assert.Equal(0, copy!.GetPixel(1, 1).Alpha);          // снаружи - прозрачно
        Assert.Equal(SKColors.Blue, copy.GetPixel(20, 20));   // внутри - как было
    }

    /// <summary>Прямоугольное выделение копируется по-прежнему целиком.</summary>
    [Fact]
    public void Copying_a_rect_selection_is_unchanged()
    {
        var doc = new Document(60, 40);
        Dot(Paper(doc), 11, 11, SKColors.Red);
        doc.Selection = new RectSelection(10, 10, 20, 20);

        using var copy = ClipboardService.ExtractForClipboard(doc);

        Assert.NotNull(copy);
        Assert.Equal(SKColors.Red, copy!.GetPixel(1, 1));
    }

    /// <summary>
    /// Размер текста слушается ползунка «Размер».
    ///
    /// Он был зашит числом 24, отдельного поля для него в панели нет, и размер надписи в
    /// программе не менялся вовсе: ползунок двигали, буквы выходили одни и те же.
    /// Множитель подобран так, чтобы значение по умолчанию давало прежние 24 пикселя.
    /// </summary>
    [Fact]
    public void Text_follows_the_size_slider()
    {
        int Ink(int toolSize)
        {
            var doc = new Document(300, 300);
            var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Black, ToolSize = toolSize, Opacity = 1f };
            var t = new TextTool();
            t.OnPointerDown(new SKPoint(20, 120), ctx);
            t.CommitText("Ab");
            return NonWhite(Paper(doc));
        }

        Assert.True(Ink(20) > Ink(4), "крупный размер обязан давать больше чернил");
    }

    /// <summary>
    /// Слой спрятали, пока окно ввода текста открыто, - писать туда нельзя.
    ///
    /// Проверка на клике от этого не спасает: между ней и ответом проходит сколько угодно
    /// времени, а панель слоёв всё это время под рукой. Текст уходил в битмап невидимого
    /// слоя: на экране не появлялось ничего, зато в ленте появлялась запись, документ
    /// считался изменённым, а надпись всплывала, стоило слой включить. Тем же правилом
    /// отсеивают работу все рисующие инструменты.
    /// </summary>
    [Fact]
    public void Text_into_a_layer_hidden_meanwhile_is_refused()
    {
        string hint = "";
        var doc = new Document(100, 60);
        var ctx = new ToolContext(doc)
        {
            PrimaryColor = SKColors.Black,
            Opacity = 1f,
            ReportHint = s => hint = s,
        };
        var t = new TextTool();
        t.OnPointerDown(new SKPoint(10, 30), ctx);
        Paper(doc).Visible = false;

        t.CommitText("Ab");

        Assert.Empty(doc.History.Commands);
        Assert.Contains("скрыт", hint);
    }

    /// <summary>И то же самое для слоя, выкрученного в полную прозрачность.</summary>
    [Fact]
    public void Text_into_a_fully_transparent_layer_is_refused()
    {
        var doc = new Document(100, 60);
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Black, Opacity = 1f };
        var t = new TextTool();
        t.OnPointerDown(new SKPoint(10, 30), ctx);
        Paper(doc).Opacity = 0f;

        t.CommitText("Ab");

        Assert.Empty(doc.History.Commands);
    }
}
