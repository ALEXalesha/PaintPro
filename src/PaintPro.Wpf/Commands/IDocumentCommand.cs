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

    /// <summary>
    /// Roughly how many bytes of pixel data this command is holding on to.
    /// <see cref="Services.HistoryManager"/> trims the oldest entries once the total gets
    /// out of hand: whole-layer ops on a photo-sized canvas keep two full bitmaps each, so
    /// a depth-only cap says nothing useful about actual memory use.
    /// Commands that hold no bitmaps can leave this at zero.
    /// </summary>
    long ApproximateBytes => 0;

    /// <summary>
    /// Изменила ли последняя <see cref="Execute"/> хоть что-нибудь.
    ///
    /// Правило «жест, от которого на холсте ничего не осталось, не попадает в ленту» жило
    /// у каждой команды по отдельности: заливка отсеивала свою пустую работу
    /// (<see cref="FillCommand.ChangedAnything"/>), стирание выделения - свою, а штрих,
    /// фигура, текст и операции над всем документом - нет. Ластик по нетронутой белой
    /// бумаге, белая кисть по белому, штрих мимо холста, кадрирование по всему холсту,
    /// отражение симметричной картинки - каждый из них не менял ни одного пикселя, но
    /// оставлял в ленте строку и объявлял документ изменённым: приложение спрашивало про
    /// сохранение после жеста, которого не было видно.
    ///
    /// Теперь вопрос задаёт сама лента, одному и тому же месту -
    /// <see cref="Services.HistoryManager.ExecuteAndPush"/>. По умолчанию «да»: команда,
    /// которая про себя ничего такого не знает, записывается как и раньше.
    /// </summary>
    bool ChangedAnything => true;
}
