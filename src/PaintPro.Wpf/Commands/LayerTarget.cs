using PaintPro.Models;

namespace PaintPro.Commands;

/// <summary>
/// Binds a command to one specific layer.
///
/// Commands used to write into whatever layer happened to be active at undo time, so
/// switching or deleting a layer between the edit and its undo dropped the pixels on the
/// wrong one. The first Execute latches the active layer's id; every later Execute/Undo
/// resolves that id, and does nothing at all if the layer no longer exists.
/// </summary>
internal static class LayerTarget
{
    public static PixelLayer? Resolve(Document doc, ref Guid layerId)
    {
        if (layerId != Guid.Empty) return doc.FindPixelLayer(layerId);
        if (doc.ActiveLayer is not PixelLayer active) return null;
        layerId = active.Id;
        return active;
    }
}
