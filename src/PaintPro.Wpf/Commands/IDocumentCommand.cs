using PaintPro.Models;

namespace PaintPro.Commands;

/// <summary>
/// Command Pattern entry for the undo/redo stack.
///
/// Each user action that mutates the document is a command. Commands store
/// *only* the diff needed to undo themselves — typically a small SKBitmap
/// covering the affected region, NOT a full canvas snapshot. This is the
/// memory-efficiency fix for antipattern #5 (PNG-snapshot undo) from
/// REWRITE_PROMPT_CSHARP.md.
/// </summary>
public interface IDocumentCommand
{
    /// <summary>Human-readable label for UI ("Draw stroke", "Fill", "Paste"…).</summary>
    string DisplayName { get; }

    /// <summary>Apply the change. Implementations must capture any state needed for Undo here.</summary>
    void Execute(Document doc);

    /// <summary>Reverse the change. Must restore the document to its pre-Execute state.</summary>
    void Undo(Document doc);
}
