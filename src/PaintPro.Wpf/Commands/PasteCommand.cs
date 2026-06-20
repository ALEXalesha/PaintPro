using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Paste a bitmap as a new FloatingPickup at a given position.
/// Undo removes the pickup AND restores any previous pickup/selection state.
///
/// The pickup is intentionally NOT committed by this command — the user typically
/// wants to drag the paste before it lands. Committing happens on tool-switch
/// or on Enter (handled by MainViewModel).
/// </summary>
public sealed class PasteCommand : IDocumentCommand
{
    private readonly SKBitmap _bitmap;
    private readonly SKPoint _topLeft;
    private FloatingPickup? _previousFloating;
    private Selection? _previousSelection;
    private FloatingPickup? _createdPickup;

    public PasteCommand(SKBitmap bitmap, SKPoint topLeft)
    {
        _bitmap = bitmap;
        _topLeft = topLeft;
    }

    public string DisplayName => "Paste";

    public void Execute(Document doc)
    {
        _previousFloating = doc.FloatingPickup;
        _previousSelection = doc.Selection;

        if (doc.FloatingPickup is not null) doc.CommitFloating();
        doc.Selection = null;

        var bbox = new SKRect(_topLeft.X, _topLeft.Y, _topLeft.X + _bitmap.Width, _topLeft.Y + _bitmap.Height);
        // Copy the bitmap so the pickup owns its pixels (it disposes them on commit/undo).
        var owned = new SKBitmap(_bitmap.Width, _bitmap.Height, _bitmap.ColorType, _bitmap.AlphaType);
        using (var c = new SKCanvas(owned)) c.DrawBitmap(_bitmap, 0, 0);

        _createdPickup = new FloatingPickup(owned, bbox)
        {
            // Mark as already-erased so the lazy-erase logic doesn't try to wipe the area where
            // the paste landed (there's nothing to "restore" — the pickup pixels are NEW).
            OriginalAreaErased = true,
        };
        doc.FloatingPickup = _createdPickup;
    }

    public void Undo(Document doc)
    {
        // Drop the pasted pickup without merging into the layer.
        if (doc.FloatingPickup == _createdPickup)
        {
            _createdPickup?.Dispose();
            doc.FloatingPickup = null;
        }
        doc.FloatingPickup = _previousFloating;
        doc.Selection = _previousSelection;
    }
}
