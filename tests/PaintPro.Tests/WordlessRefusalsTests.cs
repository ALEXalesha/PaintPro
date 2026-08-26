using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Отказы, которые оставались без единого слова. Каждый из них неотличим от сломанной
/// программы: пользователь делает жест, на экране не происходит ничего, и понять, почему,
/// нельзя. Остальные отказы объясняют себя строкой в статусбаре с 1.19.0 - эти четыре
/// тогда не нашлись.
/// </summary>
public class WordlessRefusalsTests
{
    [Fact]
    public void A_fill_with_transparent_ink_says_so()
    {
        // Кисть, фигура и текст про нулевую прозрачность сообщают; заливка - единственная
        // из рисующих, кто молчал.
        string hint = "";
        var doc = new Document(40, 40);
        var ctx = new ToolContext(doc)
        {
            PrimaryColor = SKColors.Red, Opacity = 0f, ReportHint = s => hint = s,
        };

        new FillTool().OnPointerDown(new SKPoint(20, 20), ctx);

        Assert.NotEqual("", hint);
        Assert.Empty(doc.History.Commands);
    }

    [Fact]
    public void A_fill_with_transparent_ink_on_an_upper_layer_says_so_too()
    {
        string hint = "";
        var doc = new Document(40, 40);
        doc.Layers.Add(new PixelLayer(40, 40, SKColors.Transparent));
        doc.ActiveLayerIndex = 1;
        var ctx = new ToolContext(doc)
        {
            PrimaryColor = SKColors.Red, Opacity = 0f, ReportHint = s => hint = s,
        };

        new FillTool().OnPointerDown(new SKPoint(20, 20), ctx);

        Assert.NotEqual("", hint);
    }

    [Fact]
    public void A_fill_with_real_ink_says_nothing()
    {
        string hint = "";
        var doc = new Document(40, 40);
        var ctx = new ToolContext(doc)
        {
            PrimaryColor = SKColors.Red, Opacity = 1f, ReportHint = s => hint = s,
        };

        new FillTool().OnPointerDown(new SKPoint(20, 20), ctx);

        Assert.Equal("", hint);
        Assert.Single(doc.History.Commands);
    }

    /// <summary>
    /// Ctrl+C без выделения копирует весь холст, а Ctrl+X не делал ничего и молчал: одна и
    /// та же пара клавиш на одном и том же документе отвечала по-разному.
    /// </summary>
    [Fact]
    public void Cutting_without_a_selection_says_so()
    {
        var vm = new MainViewModel();

        vm.CutSelectionCommand.Execute(null);

        Assert.NotEqual("", vm.StatusHint);
        Assert.Empty(vm.Document.History.Commands);
    }

    /// <summary>
    /// В новом документе слой ровно один, он же бумага. Проверка «слой в стопке один»
    /// стояла ПЕРЕД объяснением про бумагу и молча съедала единственный случай, когда она
    /// вообще срабатывает.
    /// </summary>
    [Fact]
    public void Removing_the_only_layer_says_so()
    {
        var vm = new MainViewModel();

        vm.RemoveLayerCommand.Execute(vm.LayerItems[0]);

        Assert.NotEqual("", vm.StatusHint);
        Assert.Single(vm.Document.Layers);
    }

    [Fact]
    public void Removing_the_paper_under_other_layers_says_so()
    {
        var vm = new MainViewModel();
        vm.AddLayerCommand.Execute(null);

        vm.RemoveLayerCommand.Execute(vm.LayerItems[0]);

        Assert.NotEqual("", vm.StatusHint);
        Assert.Equal(2, vm.Document.Layers.Count);
    }

    /// <summary>
    /// Хоткеи «[» и «]» мало кому известны, и молчание в ответ читается как «не работает».
    /// </summary>
    [Fact]
    public void The_rotate_hotkey_without_a_selection_says_so()
    {
        var vm = new MainViewModel();

        vm.RotateFloatingCommand.Execute("90");

        Assert.NotEqual("", vm.StatusHint);
        Assert.Null(vm.Document.FloatingPickup);
    }

    [Fact]
    public void The_rotate_hotkey_with_a_selection_says_nothing()
    {
        var vm = new MainViewModel();
        using (var c = new SKCanvas(((PixelLayer)vm.Document.Layers[0]).Bitmap))
            c.DrawRect(new SKRect(10, 10, 60, 60), new SKPaint { Color = SKColors.Red });
        vm.Document.Selection = new RectSelection(10, 10, 50, 50);

        vm.RotateFloatingCommand.Execute("90");

        Assert.Equal("", vm.StatusHint);
        Assert.NotNull(vm.Document.FloatingPickup);
    }

    /// <summary>Delete без выделения - жест ни о чём, и говорить тут не о чем.</summary>
    [Fact]
    public void Delete_without_a_selection_stays_silent()
    {
        var vm = new MainViewModel();

        vm.DeleteSelectionCommand.Execute(null);

        Assert.Equal("", vm.StatusHint);
    }
}
