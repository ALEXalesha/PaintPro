using CommunityToolkit.Mvvm.ComponentModel;
using PaintPro.Commands;
using PaintPro.Models;

namespace PaintPro.Services;

/// <summary>
/// Undo/redo history. Stored as a single chronological list of commands plus a
/// <see cref="Cursor"/> marking how many of them are currently applied:
/// commands[0 .. Cursor-1] are live, commands[Cursor ..] are redoable.
///
/// This list+cursor model (vs. two stacks) lets the UI show the whole timeline and
/// jump to any point — see <see cref="JumpTo"/>. Pushing a new command after an undo
/// drops the redoable tail, the standard undo semantics.
///
/// Bound to the UI via <see cref="CanUndo"/>/<see cref="CanRedo"/>; the <see cref="Changed"/>
/// event fires after every mutation so a history panel can rebuild its rows.
/// </summary>
public partial class HistoryManager : ObservableObject
{
    private readonly List<IDocumentCommand> _commands = new();
    private int _cursor; // commands[0.._cursor-1] are applied

    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;

    /// <summary>Cap on history depth; oldest entries are dropped past this.</summary>
    public int MaxDepth { get; set; } = 1000;

    /// <summary>All recorded commands in chronological order (applied + redoable).</summary>
    public IReadOnlyList<IDocumentCommand> Commands => _commands;

    /// <summary>How many commands are currently applied. Entries at index &gt;= Cursor are redoable.</summary>
    public int Cursor => _cursor;

    public int UndoDepth => _cursor;
    public int RedoDepth => _commands.Count - _cursor;

    /// <summary>Raised after any change to the timeline or cursor (push/undo/redo/jump/clear).</summary>
    public event Action? Changed;

    /// <summary>
    /// Push an already-executed command. Drops the redoable tail, trims oldest entries
    /// past <see cref="MaxDepth"/>. Use <see cref="ExecuteAndPush"/> to run Execute and push together.
    /// </summary>
    public void Push(IDocumentCommand cmd)
    {
        if (_cursor < _commands.Count)
            _commands.RemoveRange(_cursor, _commands.Count - _cursor);
        _commands.Add(cmd);
        _cursor = _commands.Count;
        Trim();
        Notify();
    }

    /// <summary>Run cmd.Execute(doc) then push. The conventional way to record an edit.</summary>
    public void ExecuteAndPush(IDocumentCommand cmd, Document doc)
    {
        cmd.Execute(doc);
        Push(cmd);
    }

    public bool Undo(Document doc)
    {
        if (_cursor == 0) return false;
        _cursor--;
        _commands[_cursor].Undo(doc);
        Notify();
        return true;
    }

    public bool Redo(Document doc)
    {
        if (_cursor >= _commands.Count) return false;
        _commands[_cursor].Execute(doc);
        _cursor++;
        Notify();
        return true;
    }

    /// <summary>
    /// Roll the document to a given timeline position. <paramref name="target"/> is the
    /// number of commands that should end up applied (0 = initial state,
    /// <see cref="Commands"/>.Count = newest). Replays Undo/Redo as needed.
    /// </summary>
    public void JumpTo(int target, Document doc)
    {
        target = Math.Clamp(target, 0, _commands.Count);
        if (target == _cursor) return;
        while (_cursor > target) { _cursor--; _commands[_cursor].Undo(doc); }
        while (_cursor < target) { _commands[_cursor].Execute(doc); _cursor++; }
        Notify();
    }

    public void Clear()
    {
        _commands.Clear();
        _cursor = 0;
        Notify();
    }

    private void Trim()
    {
        int overflow = _commands.Count - MaxDepth;
        if (overflow <= 0) return;
        // Drop the oldest commands; shift the cursor to keep pointing at the same edit.
        _commands.RemoveRange(0, overflow);
        _cursor = Math.Max(0, _cursor - overflow);
    }

    private void Notify()
    {
        CanUndo = _cursor > 0;
        CanRedo = _cursor < _commands.Count;
        Changed?.Invoke();
    }
}
