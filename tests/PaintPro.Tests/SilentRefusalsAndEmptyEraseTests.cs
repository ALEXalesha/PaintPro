using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Жесты, которые ничего не делают: стирание там, где стирать нечего, рамка меньше
/// допустимой, кадрирование по такой же рамке и вставка из пустого буфера обмена.
/// Каждый из них раньше либо молчал, либо оставлял в ленте запись про ничего.
/// </summary>
public class SilentRefusalsAndEmptyEraseTests
{
    // ───────── пустое стирание ─────────

    [Fact]
    public void Delete_over_untouched_paper_records_nothing()
    {
        var vm = new MainViewModel();
        vm.Document.Selection = new RectSelection(10, 10, 50, 50);

        vm.DeleteSelectionCommand.Execute(null);

        Assert.Empty(vm.Document.History.Commands);
        Assert.Null(vm.Document.Selection);
    }

    [Fact]
    public void Delete_over_a_blank_upper_layer_records_nothing()
    {
        var vm = new MainViewModel();
        vm.AddLayerCommand.Execute(null);
        int before = vm.Document.History.Commands.Count;

        vm.Document.Selection = new RectSelection(10, 10, 50, 50);
        vm.DeleteSelectionCommand.Execute(null);

        Assert.Equal(before, vm.Document.History.Commands.Count);
    }

    [Fact]
    public void Delete_over_a_blank_area_leaves_the_document_clean()
    {
        var vm = new MainViewModel();
        vm.Document.History.MarkSaved();

        vm.Document.Selection = new RectSelection(10, 10, 50, 50);
        vm.DeleteSelectionCommand.Execute(null);

        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Delete_that_does_erase_something_is_still_recorded()
    {
        var vm = new MainViewModel();
        var paper = (PixelLayer)vm.Document.Layers[0];
        using (var c = new SKCanvas(paper.Bitmap))
            c.DrawRect(new SKRect(10, 10, 40, 40), new SKPaint { Color = SKColors.Red });

        vm.Document.Selection = new RectSelection(10, 10, 30, 30);
        vm.DeleteSelectionCommand.Execute(null);

        Assert.Single(vm.Document.History.Commands);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void An_erase_that_changes_nothing_says_so()
    {
        var doc = new Document(20, 20);
        var cmd = new EraseRegionCommand(new SKRectI(2, 2, 12, 12), null, SKColors.White);

        cmd.Execute(doc);

        Assert.False(cmd.ChangedAnything);
        cmd.Dispose();
    }

    // ───────── слишком мелкие рамки ─────────

    [Fact]
    public void A_selection_too_small_to_keep_explains_itself()
    {
        var vm = new MainViewModel();
        var tool = new SelectTool();

        tool.OnPointerDown(new SKPoint(10, 10), vm.ToolContext);
        tool.OnPointerMove(new SKPoint(12, 12), vm.ToolContext);
        tool.OnPointerUp(new SKPoint(12, 12), vm.ToolContext);

        Assert.Null(vm.Document.Selection);
        Assert.NotEqual("", vm.StatusHint);
    }

    /// <summary>
    /// Клик без перетаскивания - это «снять выделение», а не промах, и объяснять тут
    /// нечего. Подсказка на каждый клик по холсту была бы хуже её отсутствия.
    /// </summary>
    [Fact]
    public void A_plain_click_says_nothing()
    {
        var vm = new MainViewModel();
        var tool = new SelectTool();

        tool.OnPointerDown(new SKPoint(10, 10), vm.ToolContext);
        tool.OnPointerUp(new SKPoint(10, 10), vm.ToolContext);

        Assert.Equal("", vm.StatusHint);
    }

    [Fact]
    public void A_quad_too_small_to_keep_explains_itself()
    {
        var vm = new MainViewModel();
        var tool = new QuadTool();

        tool.OnPointerDown(new SKPoint(10, 10), vm.ToolContext);
        tool.OnPointerMove(new SKPoint(12, 13), vm.ToolContext);
        tool.OnPointerUp(new SKPoint(12, 13), vm.ToolContext);

        Assert.Null(vm.Document.Selection);
        Assert.NotEqual("", vm.StatusHint);
    }

    [Fact]
    public void A_crop_too_small_to_apply_explains_itself()
    {
        var vm = new MainViewModel();
        var tool = new CropTool();

        tool.OnPointerDown(new SKPoint(10, 10), vm.ToolContext);
        tool.OnPointerMove(new SKPoint(12, 12), vm.ToolContext);
        tool.OnPointerUp(new SKPoint(12, 12), vm.ToolContext);

        Assert.Equal(Document.DefaultWidth, vm.Document.CanvasWidth);
        Assert.NotEqual("", vm.StatusHint);
    }

    [Fact]
    public void A_crop_that_does_apply_is_still_silent()
    {
        var vm = new MainViewModel();
        var tool = new CropTool();

        tool.OnPointerDown(new SKPoint(10, 10), vm.ToolContext);
        tool.OnPointerMove(new SKPoint(60, 60), vm.ToolContext);
        tool.OnPointerUp(new SKPoint(60, 60), vm.ToolContext);
        Assert.True(tool.Apply(vm.ToolContext)); // кадрирование ждёт подтверждения (1.28.0)

        Assert.Equal(50, vm.Document.CanvasWidth);
        Assert.Equal("", vm.StatusHint);
    }

    // ───────── пустой буфер обмена ─────────

    [Fact]
    public void An_empty_clipboard_explains_itself()
    {
        var vm = new MainViewModel();

        vm.ApplyClipboard(new ClipboardOutcome(ClipboardStatus.Empty));

        Assert.NotEqual("", vm.StatusHint);
        Assert.Empty(vm.Document.History.Commands);
    }

    [Fact]
    public void A_busy_clipboard_still_explains_itself()
    {
        var vm = new MainViewModel();

        vm.ApplyClipboard(new ClipboardOutcome(ClipboardStatus.Busy));

        Assert.NotEqual("", vm.StatusHint);
    }

    [Fact]
    public void A_clipboard_with_a_picture_still_pastes()
    {
        var vm = new MainViewModel();
        var bmp = new SKBitmap(10, 10);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Red);

        vm.ApplyClipboard(new ClipboardOutcome(ClipboardStatus.Ok, bmp));

        Assert.NotNull(vm.Document.FloatingPickup);
    }
}
