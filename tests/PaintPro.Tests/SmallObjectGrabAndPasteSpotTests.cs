using PaintPro.Commands;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.ViewModels;
using SkiaSharp;
using Xunit;

namespace PaintPro.Tests;

/// <summary>
/// Ручки маленького поднятого объекта и место, куда ложится вставка. И то и другое —
/// про размер: восемь ручек по двенадцать экранных пикселей накрывали мелкий объект
/// целиком, а отступ вставки в двадцать пикселей уносил её с маленького холста совсем.
/// </summary>
public class SmallObjectGrabAndPasteSpotTests
{
    private const double Preferred = 12;   // CanvasView.HandleSize

    private static FloatingPickup Pickup(float side)
        => new(new SKBitmap((int)side, (int)side), new SKRect(10, 10, 10 + side, 10 + side));

    /// <summary>Накрывает ли ручка середину объекта — то есть можно ли его вообще потащить.</summary>
    private static bool MiddleIsFree(FloatingPickup fp, double zoom)
    {
        double size = PickupOps.HandleSize(fp, zoom, Preferred);
        double top = fp.Y * zoom;                               // ручка «север»
        double middle = (fp.Y + fp.Height / 2f) * zoom;
        return middle > top + size / 2;
    }

    [Fact]
    public void A_small_object_can_still_be_grabbed_by_its_body()
    {
        Assert.True(MiddleIsFree(Pickup(12), zoom: 1.0));
    }

    [Fact]
    public void The_smallest_selection_can_still_be_grabbed_by_its_body()
    {
        // Нижняя граница выделения - 4 пикселя (SelectTool.OnPointerUp).
        Assert.True(MiddleIsFree(Pickup(4), zoom: 1.0));
    }

    [Fact]
    public void A_zoomed_out_object_can_still_be_grabbed_by_its_body()
    {
        // Сто пикселей документа при четверти натуральной величины - 25 на экране.
        Assert.True(MiddleIsFree(Pickup(100), zoom: 0.25));
    }

    [Fact]
    public void A_big_object_keeps_the_usual_handles()
    {
        Assert.Equal(Preferred, PickupOps.HandleSize(Pickup(200), 1.0, Preferred), 3);
    }

    [Fact]
    public void Handles_never_shrink_away_completely()
    {
        Assert.True(PickupOps.HandleSize(Pickup(4), 0.1, Preferred) >= 3);
    }

    // ───────── куда ложится вставка ─────────

    [Fact]
    public void A_paste_onto_a_big_canvas_keeps_its_inset()
    {
        Assert.Equal(new SKPoint(20, 20), MainViewModel.PasteOrigin(900, 600, 100, 80));
    }

    [Fact]
    public void A_paste_onto_a_tiny_canvas_stays_on_it()
    {
        Assert.Equal(new SKPoint(8, 8), MainViewModel.PasteOrigin(16, 16, 8, 8));
    }

    [Fact]
    public void A_paste_bigger_than_the_canvas_starts_at_the_corner()
    {
        Assert.Equal(new SKPoint(0, 0), MainViewModel.PasteOrigin(16, 16, 40, 40));
    }

    [Fact]
    public void A_paste_onto_a_tiny_canvas_is_visible()
    {
        var vm = new MainViewModel();
        vm.Document.History.ExecuteAndPush(new ResizeCanvasCommand(16, 16), vm.Document);
        using var img = new SKBitmap(8, 8);
        using (var c = new SKCanvas(img)) c.Clear(SKColors.Red);

        vm.PasteBitmap(img);

        var fp = vm.Document.FloatingPickup;
        Assert.NotNull(fp);
        Assert.True(Document.PickupBounds(fp!, 16, 16).HasArea());
    }

    [Fact]
    public void A_paste_onto_a_tiny_canvas_lands_when_committed()
    {
        var vm = new MainViewModel();
        vm.Document.History.ExecuteAndPush(new ResizeCanvasCommand(16, 16), vm.Document);
        using var img = new SKBitmap(8, 8);
        using (var c = new SKCanvas(img)) c.Clear(SKColors.Red);

        vm.PasteBitmap(img);
        vm.Document.CommitFloating();

        // Прижатие оставило запись, а не вычеркнуло вставку как «прижимать нечего».
        Assert.Contains(vm.Document.History.Commands, c => c.DisplayName == "Вставка");
        Assert.Equal(SKColors.Red, ((PixelLayer)vm.Document.Layers[0]).Bitmap.GetPixel(10, 10));
    }
}
