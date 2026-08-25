using System.IO;
using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Картинка, которая не влезет в документ; панель слоёв, отставшая от модели; строка
/// «Исходное состояние», обещающая не то; и запись прижатия, описывающая слой не на тот
/// момент.
/// </summary>
public class HugeImageAndPanelSyncTests
{
    // ───────── слишком большая картинка ─────────

    /// <summary>
    /// PNG с подменённым габаритом в заголовке: пикселей в нём столько же, сколько было,
    /// а IHDR обещает огромную картинку. Ровно то, на чём приложение раньше падало
    /// нехваткой памяти — проверка обязана сработать по заголовку, до раскодирования.
    /// </summary>
    private static string WriteFakeHugePng(int width, int height)
    {
        using var bmp = new SKBitmap(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Red);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();

        // Сигнатура (8 байт) + длина чанка (4) + "IHDR" (4) = ширина лежит с 16-го байта.
        void PutBe(int offset, int value)
        {
            bytes[offset + 0] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }
        PutBe(16, width);
        PutBe(20, height);
        // CRC считается по имени чанка и его данным: "IHDR" + 13 байт, то есть с 12-го по 28-й.
        PutBe(29, unchecked((int)Crc32(bytes, 12, 17)));

        var path = Path.Combine(Path.GetTempPath(), $"paintpro-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static uint Crc32(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    [Fact]
    public void An_image_too_big_for_the_document_is_refused_by_its_header()
    {
        var path = WriteFakeHugePng(25000, 25000);
        try
        {
            Assert.False(FileService.IsOpenable(path, out int w, out int h));
            Assert.Equal(25000, w);
            Assert.Equal(25000, h);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Opening_an_image_too_big_reports_a_failure_instead_of_dying()
    {
        var path = WriteFakeHugePng(25000, 25000);
        try
        {
            var outcome = new FileService().OpenImage(path);
            Assert.Equal(OpenStatus.Failed, outcome.Status);
            Assert.NotNull(outcome.Error);
            Assert.Null(outcome.Bitmap);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_ordinary_image_is_still_openable()
    {
        var path = WriteFakeHugePng(4, 4);   // габарит подменён на настоящий
        try
        {
            Assert.True(FileService.IsOpenable(path, out _, out _));
            var outcome = new FileService().OpenImage(path);
            Assert.Equal(OpenStatus.Ok, outcome.Status);
            outcome.Bitmap?.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_refused_open_does_not_become_the_save_target()
    {
        var path = WriteFakeHugePng(25000, 25000);
        try
        {
            var svc = new FileService();
            svc.OpenImage(path);
            Assert.Null(svc.LastOpenedPath);
        }
        finally { File.Delete(path); }
    }

    // ───────── панель слоёв против модели ─────────

    [Fact]
    public void New_document_refreshes_the_layer_row()
    {
        var vm = new MainViewModel();
        vm.LayerItems[0].Visible = false;

        vm.ResetDocument();

        Assert.True(vm.Document.Layers[0].Visible);
        Assert.True(vm.LayerItems[0].Visible);
    }

    [Fact]
    public void New_document_refreshes_the_layer_opacity_row()
    {
        var vm = new MainViewModel();
        vm.LayerItems[0].Opacity = 0.2;

        vm.ResetDocument();

        Assert.Equal(1f, vm.Document.Layers[0].Opacity, 3);
        Assert.Equal(1.0, vm.LayerItems[0].Opacity, 3);
    }

    // ───────── честная лента ─────────

    [Fact]
    public void The_first_history_row_stops_promising_the_beginning_after_a_trim()
    {
        var vm = new MainViewModel();
        vm.Document.History.MaxDepth = 3;
        for (int i = 0; i < 6; i++)
        {
            var t = new BrushTool();
            t.OnPointerDown(new SKPoint(10 + i, 10), vm.ToolContext);
            t.OnPointerUp(new SKPoint(20 + i, 20), vm.ToolContext);
        }

        Assert.True(vm.Document.History.Trimmed);
        Assert.NotEqual("Исходное состояние", vm.HistoryItems[0].Label);
    }

    [Fact]
    public void The_first_history_row_still_promises_the_beginning_while_nothing_was_dropped()
    {
        var vm = new MainViewModel();
        var t = new BrushTool();
        t.OnPointerDown(new SKPoint(10, 10), vm.ToolContext);
        t.OnPointerUp(new SKPoint(20, 20), vm.ToolContext);

        Assert.False(vm.Document.History.Trimmed);
        Assert.Equal("Исходное состояние", vm.HistoryItems[0].Label);
    }

    [Fact]
    public void Clearing_the_history_makes_the_first_row_honest_again()
    {
        var doc = new Document(8, 8);
        doc.History.MaxDepth = 1;
        doc.History.ExecuteAndPush(new ClearCanvasCommand(), doc);
        doc.History.ExecuteAndPush(new ClearCanvasCommand(), doc);
        Assert.True(doc.History.Trimmed);

        doc.History.Clear();

        Assert.False(doc.History.Trimmed);
    }

    // ───────── запись прижатия описывает слой на момент прижатия ─────────

    /// <summary>
    /// Между подъёмом и прижатием слой может измениться, и «до» в записи прижатия обязано
    /// это учитывать. Снимок на момент подъёма про такие правки не знает: отмена прижатия
    /// откатывала их заодно.
    /// </summary>
    [Fact]
    public void An_edit_made_while_the_object_is_held_survives_the_commit()
    {
        var reference = Painted();
        Stroke(reference);
        using var expected = Snap(reference);

        var doc = Painted();
        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));
        var fp = doc.FloatingPickup!;
        PickupOps.EnsureLazyErase(doc, fp);
        PickupOps.Translate(fp, 15, 15);
        Stroke(doc);
        doc.CommitFloating();

        doc.History.Undo(doc);

        using var actual = Snap(doc);
        Assert.Equal(0, Diff(expected, actual));
    }

    /// <summary>Обычный случай — ничего постороннего не происходило — не изменился ни на пиксель.</summary>
    [Fact]
    public void An_ordinary_commit_still_undoes_exactly()
    {
        var doc = Painted();
        using var before = Snap(doc);

        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));
        var fp = doc.FloatingPickup!;
        PickupOps.EnsureLazyErase(doc, fp);
        PickupOps.Translate(fp, 12, 9);
        doc.CommitFloating();
        doc.History.Undo(doc);

        using var after = Snap(doc);
        Assert.Equal(0, Diff(before, after));
    }

    /// <summary>
    /// Снимок пересматривается перед стиранием, а не при подъёме: Escape обязан вернуть
    /// слой к тому, чем он был непосредственно перед выкусыванием исходной области.
    /// </summary>
    [Fact]
    public void Escape_puts_back_the_layer_as_it_was_before_the_erase()
    {
        var doc = Painted();
        PickupOps.PromoteRect(doc, new SKRect(10, 10, 30, 30));
        var fp = doc.FloatingPickup!;
        Stroke(doc);                       // правка легла до первого сдвига
        using var expected = Snap(doc);

        PickupOps.EnsureLazyErase(doc, fp);
        PickupOps.Translate(fp, 15, 15);
        doc.CancelFloating();

        using var actual = Snap(doc);
        Assert.Equal(0, Diff(expected, actual));
    }

    private static Document Painted()
    {
        var doc = new Document(60, 60);
        var paper = (PixelLayer)doc.Layers[0];
        using var c = new SKCanvas(paper.Bitmap);
        using var p = new SKPaint { Color = SKColors.Red, IsAntialias = false };
        c.DrawRect(new SKRect(10, 10, 30, 30), p);
        p.Color = SKColors.Blue;
        c.DrawRect(new SKRect(25, 25, 50, 45), p);
        return doc;
    }

    private static void Stroke(Document doc)
    {
        var ctx = new ToolContext(doc) { PrimaryColor = SKColors.Lime, ToolSize = 6, Opacity = 1f };
        var t = new BrushTool();
        t.OnPointerDown(new SKPoint(5, 50), ctx);
        t.OnPointerMove(new SKPoint(50, 55), ctx);
        t.OnPointerUp(new SKPoint(50, 55), ctx);
    }

    private static SKBitmap Snap(Document doc)
    {
        var l = (PixelLayer)doc.Layers[0];
        return l.ExtractRegion(new SKRectI(0, 0, l.Width, l.Height));
    }

    private static int Diff(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return int.MaxValue;
        int n = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) n++;
        return n;
    }
}
