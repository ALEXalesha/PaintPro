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
        if (ctx.Document.ActiveLayer is not PixelLayer pl) return;
        int x = (int)position.X, y = (int)position.Y;
        if (x < 0 || y < 0 || x >= pl.Width || y >= pl.Height) return;
        var picked = pl.Bitmap.GetPixel(x, y);
        ctx.PrimaryColor = picked;
        ColorPicked?.Invoke(picked);
    }

    public void OnPointerMove(SKPoint position, ToolContext ctx) { }
    public void OnPointerUp(SKPoint position, ToolContext ctx) { }
}
