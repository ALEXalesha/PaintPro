namespace PaintPro.Models;

/// <summary>
/// Single source of truth for document edit state.
/// Replaces the 6+ boolean flags (drawing, draggingFloating, resizingFloating,
/// movingSelection, warpingQuad, cropping) from the Electron version — see
/// REWRITE_PROMPT_CSHARP.md §"7. State.drawing + state.draggingFloating + ...".
/// All transitions go through Document.TransitionMode so invariants stay enforced.
/// </summary>
public enum DocumentMode
{
    /// <summary>Nothing selected, no active drag/edit. Default resting state.</summary>
    Idle,

    /// <summary>Rectangular selection exists, no floating pickup yet.</summary>
    SelectionRect,

    /// <summary>4-point polygon selection exists, no floating pickup yet.</summary>
    SelectionPolygon,

    /// <summary>Pickup is lifted; move/scale/rotate in progress.</summary>
    FloatingActive,

    /// <summary>Shape tool drag in progress (rect, ellipse, line, etc.).</summary>
    DrawingShape,

    /// <summary>Crop tool active.</summary>
    Cropping,
}
