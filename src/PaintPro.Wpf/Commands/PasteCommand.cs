using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Paste a bitmap as a new FloatingPickup at a given position.
/// Undo removes the pickup AND restores any previous pickup/selection state.
///
/// The pickup is intentionally NOT committed by this command — the user typically
/// wants to drag the paste before it lands. Committing happens on tool-switch
/// or on Enter, and records its own diff so it stays undoable.
/// </summary>
public sealed class PasteCommand : IDocumentCommand
{
    private readonly SKBitmap _bitmap;
    private readonly SKPoint _topLeft;
    private Selection? _previousSelection;
    private FloatingPickup? _createdPickup;

    /// <param name="bitmap">Source pixels. Copied here, so the caller may dispose its own bitmap.</param>
    public PasteCommand(SKBitmap bitmap, SKPoint topLeft)
    {
        // Own a copy: the caller disposes its bitmap right after the first Execute, and a
        // redo would otherwise read a freed SKBitmap.
        _bitmap = new SKBitmap(bitmap.Width, bitmap.Height, bitmap.ColorType, bitmap.AlphaType);
        using (var c = new SKCanvas(_bitmap)) c.DrawBitmap(bitmap, 0, 0);
        _topLeft = topLeft;
    }

    public string DisplayName => "Paste";

    public void Execute(Document doc)
    {
        _previousSelection = doc.Selection;

        // Any pickup already in flight belongs to an earlier edit; land it before pasting.
        if (doc.FloatingPickup is not null) doc.CommitFloating();
        doc.Selection = null;

        var bbox = new SKRect(_topLeft.X, _topLeft.Y, _topLeft.X + _bitmap.Width, _topLeft.Y + _bitmap.Height);
        var owned = new SKBitmap(_bitmap.Width, _bitmap.Height, _bitmap.ColorType, _bitmap.AlphaType);
        using (var c = new SKCanvas(owned)) c.DrawBitmap(_bitmap, 0, 0);

        _createdPickup = new FloatingPickup(owned, bbox)
        {
            // Nothing was lifted off the layer, so there is no source area to lazily erase.
            OriginalAreaErased = true,
            CommitLabel = "Вставка",
            SourceLayerId = doc.ActiveLayer.Id,
        };
        doc.FloatingPickup = _createdPickup;
    }

    public void Undo(Document doc)
    {
        // Drop the pasted pickup without merging into the layer. If it was already
        // committed the merge has its own diff command sitting later in the timeline,
        // and there is nothing for us to undo here.
        if (ReferenceEquals(doc.FloatingPickup, _createdPickup))
        {
            _createdPickup?.Dispose();
            doc.FloatingPickup = null;
        }
        _createdPickup = null;
        doc.Selection = _previousSelection;
    }
}
