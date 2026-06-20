using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Reset the active layer to a solid fill (default white) AND clear any pending
/// floating pickup + selection. Directly addresses antipattern #1 from the spec
/// — every transient state slice must be reset, not just the visible bitmap.
/// </summary>
public sealed class ClearCanvasCommand : IDocumentCommand
{
    private readonly SKColor _fill;
    private SKBitmap? _previousBitmap;
    private FloatingPickup? _previousFloating;
    private Selection? _previousSelection;

    public ClearCanvasCommand(SKColor? fill = null) => _fill = fill ?? SKColors.White;

    public string DisplayName => "Clear canvas";

    public void Execute(Document doc)
    {
        if (doc.ActiveLayer is PixelLayer pl)
        {
            _previousBitmap = pl.ExtractRegion(new SKRectI(0, 0, pl.Width, pl.Height));
            pl.Clear(_fill);
        }
        _previousFloating = doc.FloatingPickup;
        _previousSelection = doc.Selection;

        // IMPORTANT: order matters — set FloatingPickup first (it would auto-clear Selection),
        // then Selection. Both end up null and Mode is recomputed to Idle.
        doc.FloatingPickup = null;
        doc.Selection = null;
    }

    public void Undo(Document doc)
    {
        if (doc.ActiveLayer is PixelLayer pl && _previousBitmap is not null)
        {
            using var canvas = new SKCanvas(pl.Bitmap);
            canvas.DrawBitmap(_previousBitmap, 0, 0);
        }
        doc.FloatingPickup = _previousFloating;
        doc.Selection = _previousSelection;
    }
}
