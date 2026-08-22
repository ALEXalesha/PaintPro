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
    private bool _applying; // true while Undo/Redo/JumpTo is walking the list

    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;

    /// <summary>Cap on history depth; oldest entries are dropped past this.</summary>
    public int MaxDepth { get; set; } = 1000;

    /// <summary>
    /// Cap on the pixel data the timeline holds, in bytes. Depth alone is a bad proxy:
    /// a rotate on a 12-megapixel document keeps two full-canvas bitmaps, about 96 MB,
    /// so twenty of them would be 2 GB while sitting nowhere near <see cref="MaxDepth"/>.
    /// </summary>
    public long MaxBytes { get; set; } = 768L * 1024 * 1024;

    /// <summary>All recorded commands in chronological order (applied + redoable).</summary>
    public IReadOnlyList<IDocumentCommand> Commands => _commands;

    /// <summary>How many commands are currently applied. Entries at index &gt;= Cursor are redoable.</summary>
    public int Cursor => _cursor;

    /// <summary>Последняя применённая команда или null. Нужна склейке подряд идущих однотипных правок.</summary>
    public IDocumentCommand? Current => _cursor > 0 ? _commands[_cursor - 1] : null;

    public int UndoDepth => _cursor;
    public int RedoDepth => _commands.Count - _cursor;

    /// <summary>
    /// Позиция курсора на момент последнего сохранения. Живёт здесь, а не во вьюмодели,
    /// потому что <see cref="Trim"/> выбрасывает старые записи и сдвигает курсор вниз:
    /// метка обязана ехать вместе с ним. Пока она хранилась снаружи, после переполнения
    /// истории курсор переставал расти, метка оставалась на прежнем числе, и приложение
    /// считало сохранённым всё, что нарисовано дальше. Уходит в минус, если сохранённое
    /// состояние вытеснено совсем: вернуться к нему уже нельзя, значит работа изменена.
    /// </summary>
    private int _savedCursor;

    /// <summary>Позиция, которой курсор не достигает никогда: сохранённого состояния больше нет в списке.</summary>
    private const int Unreachable = -1;

    /// <summary>Запомнить текущую позицию как сохранённую.</summary>
    public void MarkSaved() => _savedCursor = _cursor;

    /// <summary>Есть ли правки, которых нет в файле.</summary>
    public bool IsDirtySinceSave => _cursor != _savedCursor;

    /// <summary>Raised after any change to the timeline or cursor (push/undo/redo/jump/clear).</summary>
    public event Action? Changed;

    /// <summary>
    /// True while an Undo/Redo/JumpTo is in progress. Side effects that would normally
    /// record their own command (Document.CommitFloating) must stay silent then.
    /// </summary>
    public bool IsApplying => _applying;

    /// <summary>
    /// Push an already-executed command. Drops the redoable tail, trims oldest entries
    /// past <see cref="MaxDepth"/>. Use <see cref="ExecuteAndPush"/> to run Execute and push together.
    ///
    /// Ignored while <see cref="IsApplying"/>: a command's Execute/Undo can indirectly
    /// trigger CommitFloating, and letting that RemoveRange the list under the walking
    /// cursor corrupts the timeline.
    /// </summary>
    public void Push(IDocumentCommand cmd)
    {
        if (_applying) return;
        DropRedoTail();
        _commands.Add(cmd);
        _cursor = _commands.Count;
        Trim();
        Notify();
    }

    /// <summary>
    /// Правка дописана в последнюю запись вместо новой (см. <see
    /// cref="Commands.LayerPropertyCommand.MergeInto"/>): курсор не двигается, и всё, что
    /// считает правки по его позиции, такого изменения не видит.
    ///
    /// Пока склейка ходила мимо истории, «выключили слой → Ctrl+S → включили обратно»
    /// дописывалось в запись, сделанную до сохранения: курсор оставался равен метке,
    /// <see cref="IsDirtySinceSave"/> отвечал false, и окно закрывалось без вопроса, а в
    /// файле слой оставался выключенным.
    /// </summary>
    public void AmendCurrent()
    {
        if (_applying || _cursor == 0) return;
        // Дописанная правка - такая же новая правка, как любая другая: хвост повтора
        // записан относительно состояния, которого больше нет.
        DropRedoTail();
        // Изменилось состояние на позиции курсора; всё, что левее, склейка не трогала.
        if (_savedCursor >= _cursor) _savedCursor = Unreachable;
        Notify();
    }

    /// <summary>
    /// Срезать записи повтора: новая правка их обесценивает.
    ///
    /// Сохранённое состояние могло лежать в срезаемом хвосте: вернуться к нему уже
    /// нельзя, а курсор при этом остаётся на прежнем числе. Пока метка оставалась на
    /// месте, «сохранили → Ctrl+Z → нарисовали заново» давало чистый документ при другом
    /// содержимом файла, и окно закрывалось без вопроса.
    /// </summary>
    private void DropRedoTail()
    {
        if (_cursor >= _commands.Count) return;
        if (_savedCursor > _cursor) _savedCursor = Unreachable;
        for (int i = _cursor; i < _commands.Count; i++) Release(_commands[i]);
        _commands.RemoveRange(_cursor, _commands.Count - _cursor);
    }

    /// <summary>Run cmd.Execute(doc) then push. The conventional way to record an edit.</summary>
    public void ExecuteAndPush(IDocumentCommand cmd, Document doc)
    {
        cmd.Execute(doc);
        Push(cmd);
    }

    public bool Undo(Document doc)
    {
        // Поднятое выделение — это правка, которой в списке ещё нет, и первый Ctrl+Z отменяет
        // именно её. Заодно это единственный способ не сломать историю: пикап держит снимок слоя
        // на момент подъёма, и после отката этот снимок относится к состоянию, которого
        // больше нет: коммит записал бы в историю diff со старыми пикселями и воскрешал штрих,
        // который уже отменили. В Electron-версии это закрыто в restoreHistory.
        //
        // Пикап вставки - исключение: его создала команда, которая уже лежит в списке.
        // Снять его отдельно значило убрать картинку с холста, оставив запись «Вставка»
        // текущей: история говорила, что вставка применена, а на холсте её не было, и
        // Ctrl+Y картинку не возвращал - курсор-то не двигался. Такой пикап снимает
        // отмена самой команды, парой строк ниже.
        if (doc.FloatingPickup is { OwnedByCommand: false }) { doc.CancelFloating(); Notify(); return true; }
        if (_cursor == 0) return false;
        _applying = true;
        try
        {
            _cursor--;
            _commands[_cursor].Undo(doc);
        }
        finally { _applying = false; }
        Notify();
        return true;
    }

    public bool Redo(Document doc)
    {
        if (_cursor >= _commands.Count) return false;
        doc.CancelFloating();
        _applying = true;
        try
        {
            _commands[_cursor].Execute(doc);
            _cursor++;
        }
        finally { _applying = false; }
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
        doc.CancelFloating();
        _applying = true;
        try
        {
            while (_cursor > target) { _cursor--; _commands[_cursor].Undo(doc); }
            while (_cursor < target) { _commands[_cursor].Execute(doc); _cursor++; }
        }
        finally { _applying = false; }
        Notify();
    }

    public void Clear()
    {
        foreach (var c in _commands) Release(c);
        _commands.Clear();
        _cursor = 0;
        _savedCursor = 0;
        Notify();
    }

    private void Trim()
    {
        int overflow = _commands.Count - MaxDepth;
        if (overflow > 0) Drop(overflow);

        // Then trim by memory. Never drop the newest entry, however heavy it is — losing
        // the undo for the edit that was just made would be worse than the memory.
        long total = 0;
        foreach (var c in _commands) total += c.ApproximateBytes;
        int dropped = 0;
        while (total > MaxBytes && _commands.Count - dropped > 1)
        {
            total -= _commands[dropped].ApproximateBytes;
            dropped++;
        }
        if (dropped > 0) Drop(dropped);
    }

    /// <summary>Drop the oldest <paramref name="count"/> commands, keeping the cursor on the same edit.</summary>
    private void Drop(int count)
    {
        // Битмапы внутри команды — нативная память, давления на сборщик она не создаёт.
        // Без явного Dispose обрезка по MaxBytes освобождала ссылку, но не память, и лимит
        // был лимитом только на бумаге.
        for (int i = 0; i < count; i++) Release(_commands[i]);
        _commands.RemoveRange(0, count);
        _cursor = Math.Max(0, _cursor - count);
        if (_savedCursor >= 0) _savedCursor -= count;
    }

    /// <summary>Отпустить пиксели, которые держала выброшенная запись.</summary>
    private static void Release(IDocumentCommand cmd)
    {
        if (cmd is IDisposable d) d.Dispose();
    }

    private void Notify()
    {
        CanUndo = _cursor > 0;
        CanRedo = _cursor < _commands.Count;
        Changed?.Invoke();
    }
}
