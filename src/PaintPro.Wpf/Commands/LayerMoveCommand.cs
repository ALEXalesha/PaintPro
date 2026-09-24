using PaintPro.Models;

namespace PaintPro.Commands;

/// <summary>
/// Перестановка слоя в стопке на одну ступень (1.28.0, как «▲ ▼» Electron-версии). Пиксели не
/// трогаются, поэтому отмена - та же перестановка обратно; активным остаётся тот же слой.
/// </summary>
public sealed class LayerMoveCommand : IDocumentCommand
{
    private readonly int _from, _to;

    public LayerMoveCommand(int from, int to) { _from = from; _to = to; }

    public string DisplayName => "Порядок слоёв";

    public void Execute(Document doc) => Move(doc, _from, _to);

    public void Undo(Document doc) => Move(doc, _to, _from);

    private static void Move(Document doc, int from, int to)
    {
        if (from < 0 || to < 0 || from >= doc.Layers.Count || to >= doc.Layers.Count) return;
        var active = doc.ActiveLayer;
        doc.Layers.Move(from, to);
        doc.ActiveLayerIndex = doc.Layers.IndexOf(active);
    }
}
