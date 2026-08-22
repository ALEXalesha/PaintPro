using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Сборка документа (что над чем лежит), смешивание заливки и стирание: скрытый слой и
/// только что вставленная картинка.
/// </summary>
public class CompositeAndEraseTests
{
    private static void Rect(PixelLayer layer, SKRect r, SKColor color)
    {
        using var c = new SKCanvas(layer.Bitmap);
        using var p = new SKPaint { Color = color, BlendMode = SKBlendMode.Src };
        c.DrawRect(r, p);
    }

    private static SKBitmap Solid(int w, int h, SKColor color)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(bmp);
        c.Clear(color);
        return bmp;
    }

    // ───────── превью и плавающий объект лежат на своём слое ─────────

    [Fact]
    public void A_pickup_lifted_from_the_bottom_layer_stays_under_the_upper_one()
    {
        var doc = new Document(40, 40);
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Верхний"), doc);
        Rect((PixelLayer)doc.Layers[1], new SKRect(0, 0, 40, 40), SKColors.Red);

        doc.ActiveLayerIndex = 0;
        doc.FloatingPickup = new FloatingPickup(Solid(20, 20, SKColors.Blue), new SKRect(10, 10, 30, 30))
        {
            SourceLayerId = doc.Layers[0].Id,
        };

        using var flat = FileService.Flatten(doc);

        // Объект подняли с нижнего слоя, значит и лежать он обязан под верхним - там же,
        // где окажется после прижатия. Пока он рисовался последним, картинка на отпускании
        // ныряла под верхний слой и прыгала на глазах.
        Assert.Equal(SKColors.Red, flat.GetPixel(20, 20));
    }

    [Fact]
    public void A_pickup_lifted_from_the_upper_layer_stays_on_top()
    {
        var doc = new Document(40, 40);
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Верхний"), doc);
        Rect((PixelLayer)doc.Layers[1], new SKRect(0, 0, 40, 40), SKColors.Red);

        doc.ActiveLayerIndex = 1;
        doc.FloatingPickup = new FloatingPickup(Solid(20, 20, SKColors.Blue), new SKRect(10, 10, 30, 30))
        {
            SourceLayerId = doc.Layers[1].Id,
        };

        using var flat = FileService.Flatten(doc);

        Assert.Equal(SKColors.Blue, flat.GetPixel(20, 20));
    }

    [Fact]
    public void A_pickup_whose_layer_is_gone_is_still_drawn()
    {
        var doc = new Document(40, 40);
        doc.FloatingPickup = new FloatingPickup(Solid(20, 20, SKColors.Blue), new SKRect(10, 10, 30, 30))
        {
            SourceLayerId = Guid.NewGuid(),   // слоя с таким id в документе нет
        };

        using var flat = FileService.Flatten(doc);

        Assert.Equal(SKColors.Blue, flat.GetPixel(20, 20));
    }

    [Fact]
    public void The_tool_preview_lands_on_the_active_layer_not_over_everything()
    {
        var doc = new Document(40, 40);
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Верхний"), doc);
        Rect((PixelLayer)doc.Layers[1], new SKRect(0, 0, 40, 40), SKColors.Red);
        doc.ActiveLayerIndex = 0;

        using var preview = Solid(40, 40, SKColors.Blue);
        var canvas = new SKBitmap(40, 40, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(canvas))
        {
            c.Clear(SKColors.White);
            doc.Render(c, preview);
        }

        // Штрих по нижнему слою не должен во время рисования лежать поверх верхнего:
        // на отпускании он всё равно окажется под ним.
        Assert.Equal(SKColors.Red, canvas.GetPixel(20, 20));
        canvas.Dispose();
    }

    // ───────── заливка смешивается с подложкой ─────────

    [Fact]
    public void A_translucent_fill_blends_with_what_is_under_it()
    {
        var doc = new Document(20, 20);
        var layer = (PixelLayer)doc.Layers[0];
        Rect(layer, new SKRect(0, 0, 20, 20), SKColors.Red);

        // Чёрный на 50%: красное обязано стать тёмно-красным, а не серым.
        doc.History.ExecuteAndPush(
            new FillCommand(new SKPointI(10, 10), SKColors.Black.WithAlpha(128)), doc);

        var px = layer.Bitmap.GetPixel(10, 10);
        Assert.Equal(255, px.Alpha);            // дыры в бумаге не появилось
        Assert.InRange(px.Red, 120, 135);       // ≈ половина исходного красного
        Assert.Equal(0, px.Green);
        Assert.Equal(0, px.Blue);
    }

    [Fact]
    public void An_opaque_fill_is_unchanged_by_the_blending()
    {
        var doc = new Document(20, 20);
        var layer = (PixelLayer)doc.Layers[0];

        doc.History.ExecuteAndPush(new FillCommand(new SKPointI(10, 10), SKColors.Red), doc);

        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(10, 10));
    }

    [Fact]
    public void A_fully_transparent_fill_changes_nothing()
    {
        var doc = new Document(20, 20);
        var layer = (PixelLayer)doc.Layers[0];
        var cmd = new FillCommand(new SKPointI(10, 10), SKColors.Black.WithAlpha(0));

        cmd.Execute(doc);

        Assert.False(cmd.ChangedAnything);
        Assert.True(layer.IsAllWhite());
    }

    // ───────── стирание ─────────

    [Fact]
    public void Delete_on_a_hidden_layer_changes_nothing_and_says_why()
    {
        var vm = new MainViewModel();
        var doc = vm.Document;
        doc.History.ExecuteAndPush(LayerStackCommand.Add(doc, "Верхний"), doc);
        var layer = (PixelLayer)doc.Layers[1];
        Rect(layer, new SKRect(0, 0, 40, 40), SKColors.Red);
        layer.Visible = false;
        doc.ActiveLayerIndex = 1;
        doc.Selection = new RectSelection(0, 0, 20, 20);
        int history = doc.History.UndoDepth;

        vm.DeleteSelectionCommand.Execute(null);

        // Стирание уходило в битмап скрытого слоя: на экране не менялось ничего, зато в
        // истории появлялась запись, а документ считался изменённым.
        Assert.Equal(SKColors.Red, layer.Bitmap.GetPixel(10, 10));
        Assert.Equal(history, doc.History.UndoDepth);
        Assert.Contains("скрыт", vm.StatusHint);
    }

    [Fact]
    public void Delete_on_a_visible_layer_still_erases()
    {
        var vm = new MainViewModel();
        var doc = vm.Document;
        Rect((PixelLayer)doc.Layers[0], new SKRect(0, 0, 40, 40), SKColors.Red);
        doc.Selection = new RectSelection(0, 0, 20, 20);

        vm.DeleteSelectionCommand.Execute(null);

        Assert.Equal(SKColors.White, ((PixelLayer)doc.Layers[0]).Bitmap.GetPixel(10, 10));
        Assert.Equal(1, doc.History.UndoDepth);
    }

    // ───────── снятие только что вставленной картинки ─────────

    private static MainViewModel VmWithPaste()
    {
        var vm = new MainViewModel();
        using var bmp = Solid(30, 30, SKColors.Blue);
        vm.Document.History.ExecuteAndPush(new PasteCommand(bmp, new SKPoint(20, 20)), vm.Document);
        return vm;
    }

    [Fact]
    public void Delete_after_paste_takes_the_paste_out_of_history_too()
    {
        var vm = VmWithPaste();
        Assert.NotNull(vm.Document.FloatingPickup);

        vm.DeleteSelectionCommand.Execute(null);

        // Раньше пикап снимался в обход команды: картинка исчезала, а запись «Вставка»
        // оставалась текущей - история врала, документ считался изменённым, и Ctrl+Y
        // картинку не возвращал.
        Assert.Null(vm.Document.FloatingPickup);
        Assert.Equal(0, vm.Document.History.Cursor);
        Assert.False(vm.IsDirty);
        Assert.True(vm.Document.History.CanRedo);
    }

    [Fact]
    public void Escape_after_paste_takes_the_paste_out_of_history_too()
    {
        var vm = VmWithPaste();

        vm.CancelFloatingCommand.Execute(null);

        Assert.Null(vm.Document.FloatingPickup);
        Assert.Equal(0, vm.Document.History.Cursor);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Redo_brings_the_dropped_paste_back()
    {
        var vm = VmWithPaste();
        vm.DeleteSelectionCommand.Execute(null);

        vm.RedoCommand.Execute(null);

        Assert.NotNull(vm.Document.FloatingPickup);
        Assert.Equal(1, vm.Document.History.Cursor);
    }

    [Fact]
    public void A_pickup_the_user_lifted_himself_is_still_dropped_the_old_way()
    {
        var vm = new MainViewModel();
        var doc = vm.Document;
        var layer = (PixelLayer)doc.Layers[0];
        Rect(layer, new SKRect(0, 0, 60, 60), SKColors.Red);
        doc.Selection = new RectSelection(10, 10, 30, 30);
        new PaintPro.Tools.SelectTool().OnPointerDown(new SKPoint(20, 20), vm.ToolContext);
        Assert.NotNull(doc.FloatingPickup);

        vm.DeleteSelectionCommand.Execute(null);

        // Такой пикап истории не принадлежит: дыра на его месте - обычная правка.
        Assert.Null(doc.FloatingPickup);
        Assert.Equal(SKColors.White, layer.Bitmap.GetPixel(20, 20));
        Assert.Equal(1, doc.History.UndoDepth);
    }

    // ───────── поворот выделения с клавиатуры ─────────

    [Fact]
    public void Bracket_lifts_the_selection_and_turns_it()
    {
        var vm = new MainViewModel();
        Rect((PixelLayer)vm.Document.Layers[0], new SKRect(0, 0, 80, 80), SKColors.Red);
        vm.Document.Selection = new RectSelection(10, 10, 40, 20);

        vm.RotateFloatingCommand.Execute("90");

        // Хоткей описан в спеке и работает в Electron-версии, а здесь не делал ничего.
        var fp = vm.Document.FloatingPickup;
        Assert.NotNull(fp);
        Assert.Equal(MathF.PI / 2f, fp!.Rotation, 4);
        Assert.True(fp.OriginalAreaErased);           // поворот - это трансформация
        Assert.Equal(ToolKind.Select, vm.ActiveTool); // повёрнутое есть чем двигать
    }

    [Fact]
    public void Shift_bracket_turns_by_fifteen_degrees_and_the_other_one_turns_back()
    {
        var vm = new MainViewModel();
        Rect((PixelLayer)vm.Document.Layers[0], new SKRect(0, 0, 80, 80), SKColors.Red);
        vm.Document.Selection = new RectSelection(10, 10, 40, 20);

        vm.RotateFloatingCommand.Execute("15");
        vm.RotateFloatingCommand.Execute("-15");

        Assert.Equal(0f, vm.Document.FloatingPickup!.Rotation, 4);
    }

    [Fact]
    public void Bracket_without_a_selection_does_nothing()
    {
        var vm = new MainViewModel();

        vm.RotateFloatingCommand.Execute("90");

        Assert.Null(vm.Document.FloatingPickup);
        Assert.Equal(0, vm.Document.History.UndoDepth);
    }

    [Fact]
    public void A_paste_that_is_no_longer_the_last_edit_is_dropped_the_old_way()
    {
        var vm = VmWithPaste();
        // Между вставкой и удалением легла другая правка: отменять её вместо вставки нельзя.
        vm.Document.History.ExecuteAndPush(
            new LayerPropertyCommand(vm.Document.Layers[0], visible: true, opacity: 0.5f), vm.Document);

        vm.DeleteSelectionCommand.Execute(null);

        Assert.Null(vm.Document.FloatingPickup);
        Assert.Equal(2, vm.Document.History.Cursor);   // чужая правка на месте
    }
}
