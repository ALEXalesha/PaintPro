using System.Windows.Input;
using PaintPro.Models;
using PaintPro.Services;
using SkiaSharp;

namespace PaintPro.Tools;

/// <summary>
/// Per-tool isolation contract. Each drawing/selection tool implements this.
/// The host (CanvasView + MainViewModel) routes pointer events to the active tool.
///
/// Tools must be **stateless across activations** — anything mutable lives in
/// <see cref="ToolContext"/> or in the tool's own private fields that get reset
/// in <see cref="OnActivate"/>. Never reach for global state.
/// </summary>
public interface ITool
{
    /// <summary>Identifier used in the toolbar (also matches the hotkey-bound enum).</summary>
    string Name { get; }

    /// <summary>Pointer pressed at this document-coords position.</summary>
    void OnPointerDown(SKPoint position, ToolContext ctx);

    /// <summary>Pointer moved (also fires when not pressed, for hover cursor).</summary>
    void OnPointerMove(SKPoint position, ToolContext ctx);

    /// <summary>Pointer released. The tool typically commits its work here as an IDocumentCommand.</summary>
    void OnPointerUp(SKPoint position, ToolContext ctx);

    /// <summary>Called when this tool becomes active.</summary>
    void OnActivate(ToolContext ctx);

    /// <summary>
    /// Called when the tool is being deactivated (user switched). The tool MUST
    /// commit any pending state (e.g. floating pickup) here — leaving partial state
    /// behind caused multiple antipatterns in the Electron version.
    /// </summary>
    void OnDeactivate(ToolContext ctx);

    /// <summary>
    /// Optional preview bitmap drawn on top of the canvas (e.g. shape rubber-band,
    /// brush ghost). Returns null if no live preview is in progress.
    /// </summary>
    SKBitmap? PreviewBitmap { get; }

    /// <summary>Cursor to show at the given position. Return null to use default.</summary>
    Cursor? GetCursor(SKPoint position);
}

/// <summary>
/// Read/write facade passed to tools. Keeps the tool from grabbing the whole Document
/// or the ViewModel — limits the blast radius if a tool does something weird.
/// </summary>
public sealed class ToolContext
{
    public Document Document { get; }
    public HistoryManager History => Document.History;

    public SKColor PrimaryColor { get; set; } = SKColors.Black;
    public SKColor SecondaryColor { get; set; } = SKColors.White;
    /// <summary>Brush/pencil/marker size in pixels.</summary>
    public float ToolSize { get; set; } = 4f;
    /// <summary>Opacity 0..1 for the next stroke.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>True while a pointer button is held — the canvas uses this to hide handles.</summary>
    public bool IsDrawing { get; set; }

    public ToolContext(Document doc) => Document = doc;
}
