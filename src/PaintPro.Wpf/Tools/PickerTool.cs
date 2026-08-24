using System.Windows.Input;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Tools;

/// <summary>Eyedropper — reads the pixel under the cursor and sets PrimaryColor.</summary>
public sealed class PickerTool : ITool
{
    public string Name => "Picker";
    public SKBitmap? PreviewBitmap => null;
    public Cursor? GetCursor(SKPoint position) => Cursors.Cross;

    /// <summary>Raised when the user picks a colour; main VM updates the palette state.</summary>
    public event Action<SKColor>? ColorPicked;

    public void OnActivate(ToolContext ctx) { }
    public void OnDeactivate(ToolContext ctx) { }

    public void OnPointerDown(SKPoint position, ToolContext ctx)
    {
        var doc = ctx.Document;
        // Floor, а не приведение к int - см. FillTool: иначе точка чуть левее или выше
        // холста читается как его левый верхний пиксель.
        int x = (int)MathF.Floor(position.X), y = (int)MathF.Floor(position.Y);
        if (x < 0 || y < 0 || x >= doc.CanvasWidth || y >= doc.CanvasHeight) return;
        // Sample the composite, not the active layer: picking a colour you can see should
        // work regardless of which layer happens to be selected.
        var picked = doc.SampleComposite(x, y);
        ctx.PrimaryColor = picked;
        ColorPicked?.Invoke(picked);
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx) { }
    public void OnPointerUp(SKPoint position, ToolContext ctx) { }
}
