using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// A before/after pixel diff over a fixed rectangular region of the active layer.
/// Used to make an already-applied edit reversible — e.g. committing a moved/resized/
/// rotated selection, where the net change spans the source and destination areas.
/// Execute re-applies the "after" pixels; Undo restores the "before" pixels.
/// </summary>
public sealed class RegionDiffCommand : IDocumentCommand
{
    private readonly SKRectI _bounds;
    private readonly SKBitmap _before;
    private readonly SKBitmap _after;

    /// <param name="before">Region pixels before the edit (command takes ownership).</param>
    /// <param name="after">Region pixels after the edit (command takes ownership).</param>
    public RegionDiffCommand(string displayName, SKRectI bounds, SKBitmap before, SKBitmap after)
    {
        DisplayName = displayName;
        _bounds = bounds;
        _before = before;
        _after = after;
    }

    public string DisplayName { get; }

    public void Execute(Document doc) => Blit(doc, _after);
    public void Undo(Document doc) => Blit(doc, _before);

    private void Blit(Document doc, SKBitmap src)
    {
        if (doc.ActiveLayer is not PixelLayer pl) return;
        using var canvas = new SKCanvas(pl.Bitmap);
        canvas.Save();
        canvas.ClipRect(new SKRect(_bounds.Left, _bounds.Top, _bounds.Right, _bounds.Bottom));
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(src, new SKPoint(_bounds.Left, _bounds.Top));
        canvas.Restore();
    }
}
