using PaintPro.Models;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Возможности, которые были только в Electron-версии и перенесены сюда в 1.28.0:
/// порядок слоёв, «Очистить холст», размер выделения в строке состояния. Поведение то же,
/// что там, вплоть до текста подсказок.
/// </summary>
public class ElectronParityTests
{
    private static void Dot(PixelLayer l, int x, int y, SKColor c)
    {
        using var canvas = new SKCanvas(l.Bitmap);
        using var p = new SKPaint { Color = c, BlendMode = SKBlendMode.Src };
        canvas.DrawRect(x, y, 4, 4, p);
    }

    private static MainViewModel WithLayers(int extra)
    {
        var vm = new MainViewModel();
        for (var i = 0; i < extra; i++) vm.AddLayerCommand.Execute(null);
        return vm;
    }

    // ───────── порядок слоёв ─────────

    [Fact]
    public void A_layer_moves_up_and_down_and_undo_puts_it_back()
    {
        var vm = WithLayers(2);
        var a = vm.Document.Layers[1];
        var b = vm.Document.Layers[2];
        Assert.True(vm.MoveLayer(1, +1));
        Assert.Same(b, vm.Document.Layers[1]);
        Assert.Same(a, vm.Document.Layers[2]);
        vm.UndoCommand.Execute(null);
        Assert.Same(a, vm.Document.Layers[1]);
        Assert.Same(b, vm.Document.Layers[2]);
    }

    [Fact]
    public void The_active_layer_stays_the_same_layer_after_a_move()
    {
        var vm = WithLayers(2);
        vm.Document.ActiveLayerIndex = 1;
        var active = vm.Document.ActiveLayer;
        vm.MoveLayer(1, +1);
        Assert.Same(active, vm.Document.ActiveLayer);
    }

    [Fact]
    public void The_paper_stays_at_the_bottom_and_says_so()
    {
        var vm = WithLayers(1);
        Assert.False(vm.MoveLayer(0, +1));
        Assert.Equal("Бумага документа всегда лежит снизу.", vm.StatusHint);
        Assert.False(vm.MoveLayer(1, -1));
        Assert.Equal("Бумага документа всегда лежит снизу.", vm.StatusHint);
        Assert.DoesNotContain(vm.Document.History.Commands, c => c.DisplayName == "Порядок слоёв");
    }

    [Fact]
    public void The_top_layer_cannot_go_higher_and_says_why()
    {
        var vm = WithLayers(2);
        Assert.False(vm.MoveLayer(2, +1));
        Assert.Equal("Слой уже с краю стопки: двигать его дальше некуда.", vm.StatusHint);
    }

    [Fact]
    public void The_panel_lists_the_top_layer_first()
    {
        var vm = WithLayers(2);
        Assert.Same(vm.Document.Layers[2], vm.LayerItemsTopFirst[0].Layer);
        Assert.Same(vm.Document.Layers[0], vm.LayerItemsTopFirst[^1].Layer);
        vm.MoveLayer(1, +1);
        Assert.Same(vm.Document.Layers[2], vm.LayerItemsTopFirst[0].Layer);
    }

    // ───────── очистить холст ─────────

    [Fact]
    public void Clearing_empties_every_layer_whitens_the_paper_and_keeps_the_layers()
    {
        var vm = WithLayers(1);
        var paper = (PixelLayer)vm.Document.Layers[0];
        var top = (PixelLayer)vm.Document.Layers[1];
        top.Name = "Верх";
        top.Opacity = 0.5f;
        Dot(paper, 10, 10, SKColors.Red);
        Dot(top, 20, 20, SKColors.Blue);

        vm.ClearCanvasNow();

        Assert.Equal(2, vm.Document.Layers.Count);
        Assert.Equal("Верх", vm.Document.Layers[1].Name);
        Assert.Equal(0.5f, vm.Document.Layers[1].Opacity, 3);
        Assert.Equal(SKColors.White, ((PixelLayer)vm.Document.Layers[0]).Bitmap.GetPixel(11, 11));
        Assert.Equal(0, ((PixelLayer)vm.Document.Layers[1]).Bitmap.GetPixel(21, 21).Alpha);

        vm.UndoCommand.Execute(null);
        Assert.Equal(SKColors.Red, ((PixelLayer)vm.Document.Layers[0]).Bitmap.GetPixel(11, 11));
        Assert.Equal(SKColors.Blue, ((PixelLayer)vm.Document.Layers[1]).Bitmap.GetPixel(21, 21));
    }

    [Fact]
    public void Clearing_is_one_entry_in_the_timeline()
    {
        var vm = new MainViewModel();
        Dot((PixelLayer)vm.Document.Layers[0], 10, 10, SKColors.Red);
        var before = vm.Document.History.Commands.Count;
        vm.ClearCanvasNow();
        Assert.Equal(before + 1, vm.Document.History.Commands.Count);
        Assert.Equal("Очистка холста", vm.Document.History.Commands[^1].DisplayName);
    }

    [Fact]
    public void Clearing_a_blank_sheet_leaves_no_entry()
    {
        // Общее правило C#-версии: правка, от которой ничего не изменилось, в ленту не идёт.
        var vm = new MainViewModel();
        var before = vm.Document.History.Commands.Count;
        vm.ClearCanvasNow();
        Assert.Equal(before, vm.Document.History.Commands.Count);
        Assert.False(vm.IsDirty);
    }

    // ───────── размер выделения ─────────

    [Fact]
    public void The_status_bar_shows_the_size_of_the_selection_and_forgets_it()
    {
        var vm = new MainViewModel();
        Assert.Equal("", vm.SelectionSizeLabel);
        vm.Document.Selection = new RectSelection(10, 20, 120, 80);
        Assert.Equal("Выделение: 120 × 80", vm.SelectionSizeLabel);
        vm.Document.Selection = null;
        Assert.Equal("", vm.SelectionSizeLabel);
    }
}
