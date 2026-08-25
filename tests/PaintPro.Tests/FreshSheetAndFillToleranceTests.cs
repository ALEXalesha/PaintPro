using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Чистый лист «Создать», допуск заливки и активный слой после отмены удаления.
/// </summary>
public class FreshSheetAndFillToleranceTests
{
    private static PixelLayer Paper(Document d) => (PixelLayer)d.Layers[0];

    private static void Rect(PixelLayer l, float x0, float y0, float x1, float y1, SKColor c)
    {
        using var canvas = new SKCanvas(l.Bitmap);
        using var p = new SKPaint { Color = c, BlendMode = SKBlendMode.Src, IsAntialias = false };
        canvas.DrawRect(new SKRect(x0, y0, x1, y1), p);
    }

    /// <summary>
    /// «Создать» возвращает и размер холста.
    ///
    /// Он оставался от прежней работы: открыл фотографию 4000x3000, нажал Ctrl+N - и
    /// получил чистый лист 4000x3000, вернуть который к обычному можно было только через
    /// диалог смены размера. Кадрирование до марки оставляло, наоборот, лоскут в полсотни
    /// пикселей. Новый документ - это чистый лист, а не старый со стёртым рисунком; ровно
    /// поэтому же «Создать» сбрасывает стопку слоёв, имя бумаги, её видимость и
    /// прозрачность.
    /// </summary>
    [Fact]
    public void New_document_returns_to_the_default_canvas_size()
    {
        var vm = new MainViewModel();
        var doc = vm.Document;
        Rect(Paper(doc), 10, 10, 40, 40, SKColors.Red);
        doc.History.ExecuteAndPush(DocumentTransform.Crop(doc, new SKRectI(0, 0, 100, 80)), doc);
        Assert.Equal(100, doc.CanvasWidth);

        vm.ResetDocument();

        Assert.Equal(Document.DefaultWidth, doc.CanvasWidth);
        Assert.Equal(Document.DefaultHeight, doc.CanvasHeight);
        Assert.Single(doc.Layers);
        Assert.True(Paper(doc).IsAllWhite());
    }

    /// <summary>Отмена «Создать» возвращает и работу, и её размер.</summary>
    [Fact]
    public void Undoing_new_document_restores_size_and_pixels()
    {
        var vm = new MainViewModel();
        var doc = vm.Document;
        Rect(Paper(doc), 10, 10, 40, 40, SKColors.Red);
        doc.History.ExecuteAndPush(DocumentTransform.Crop(doc, new SKRectI(0, 0, 100, 80)), doc);

        vm.ResetDocument();
        vm.UndoCommand.Execute(null);

        Assert.Equal(100, doc.CanvasWidth);
        Assert.Equal(80, doc.CanvasHeight);
        Assert.Equal(SKColors.Red, Paper(doc).Bitmap.GetPixel(20, 20));
    }

    /// <summary>Слои после отмены «Создать» строятся под вернувшийся холст, а не под чистый лист.</summary>
    [Fact]
    public void Layers_after_undo_match_the_restored_canvas()
    {
        var vm = new MainViewModel();
        var doc = vm.Document;
        vm.AddLayerCommand.Execute(null);
        doc.History.ExecuteAndPush(DocumentTransform.Crop(doc, new SKRectI(0, 0, 100, 80)), doc);

        vm.ResetDocument();
        vm.UndoCommand.Execute(null);

        Assert.Equal(2, doc.Layers.Count);
        foreach (var l in doc.Layers)
        {
            Assert.Equal(doc.CanvasWidth, l.Width);
            Assert.Equal(doc.CanvasHeight, l.Height);
        }
    }

    /// <summary>
    /// Заливка не спотыкается о разницу в один-два уровня яркости.
    ///
    /// Точное совпадение цвета означало, что по фотографии заливается ровно один пиксель:
    /// небо на снимке состоит из почти одинаковых, но не равных цветов, и клик по нему не
    /// делал ничего заметного. Допуск тот же, что в Electron-версии (colorsMatch, tol = 2).
    /// </summary>
    [Fact]
    public void Fill_tolerates_near_identical_colours()
    {
        var doc = new Document(20, 20);
        var paper = Paper(doc);
        Rect(paper, 5, 5, 6, 6, new SKColor(254, 254, 254));

        var cmd = new FillCommand(new SKPointI(0, 0), SKColors.Red);
        cmd.Execute(doc);

        Assert.Equal(SKColors.Red, paper.Bitmap.GetPixel(5, 5));
    }

    /// <summary>Настоящую границу допуск не переливает.</summary>
    [Fact]
    public void Fill_still_stops_at_a_real_edge()
    {
        var doc = new Document(30, 30);
        Rect(Paper(doc), 0, 14, 30, 16, SKColors.Black);

        new FillCommand(new SKPointI(5, 5), SKColors.Red).Execute(doc);

        Assert.Equal(SKColors.Red, Paper(doc).Bitmap.GetPixel(5, 5));
        Assert.Equal(SKColors.Black, Paper(doc).Bitmap.GetPixel(5, 15));
        Assert.Equal(SKColors.White, Paper(doc).Bitmap.GetPixel(5, 25));
    }

    /// <summary>
    /// Заливка по плавному градиенту завершается.
    ///
    /// С допуском залитый пиксель может остаться в его пределах от исходного цвета -
    /// соседи кладут его в стек снова и снова, и без карты пройденных программа зависает.
    /// В Electron-версии карта посещённых стоит ровно по этой причине.
    /// </summary>
    [Fact]
    public void Fill_on_a_gradient_terminates()
    {
        var doc = new Document(80, 80);
        using (var c = new SKCanvas(Paper(doc).Bitmap))
        using (var p = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(80, 80),
                new[] { SKColors.White, SKColors.Black }, null, SKShaderTileMode.Clamp),
        })
            c.DrawRect(new SKRect(0, 0, 80, 80), p);

        var cmd = new FillCommand(new SKPointI(40, 40), new SKColor(125, 125, 125, 20));
        cmd.Execute(doc);

        Assert.True(cmd.ChangedAnything);
    }

    /// <summary>
    /// Отмена удаления слоя возвращает и то, какой слой был активен.
    ///
    /// Активным становился восстановленный: пользователь удалил верхний слой, вернул его
    /// отменой и продолжал рисовать - но уже не там, где рисовал минуту назад, а по
    /// только что воскресшему. Номер активного слоя удаление записывает, а отмена его
    /// не читала вовсе.
    /// </summary>
    [Fact]
    public void Undo_of_a_layer_removal_restores_the_active_layer()
    {
        var vm = new MainViewModel();
        vm.AddLayerCommand.Execute(null);
        vm.AddLayerCommand.Execute(null);
        vm.Document.ActiveLayerIndex = 0;

        vm.RemoveLayerCommand.Execute(vm.LayerItems[2]);
        Assert.Equal(0, vm.Document.ActiveLayerIndex);

        vm.UndoCommand.Execute(null);

        Assert.Equal(3, vm.Document.Layers.Count);
        Assert.Equal(0, vm.Document.ActiveLayerIndex);
        Assert.True(vm.LayerItems[0].IsActive);
    }

    /// <summary>А добавление слоя по-прежнему делает активным именно новый.</summary>
    [Fact]
    public void Adding_a_layer_still_selects_it()
    {
        var vm = new MainViewModel();
        vm.AddLayerCommand.Execute(null);
        Assert.Equal(1, vm.Document.ActiveLayerIndex);

        vm.UndoCommand.Execute(null);
        vm.RedoCommand.Execute(null);

        Assert.Equal(1, vm.Document.ActiveLayerIndex);
    }
}
