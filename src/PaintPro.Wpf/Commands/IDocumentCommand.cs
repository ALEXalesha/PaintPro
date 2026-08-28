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

    /// <summary>
    /// Кладёт ли запись ГОТОВЫЙ снимок пикселей поверх того, что там сейчас, - вместо того
    /// чтобы сложиться с ним.
    ///
    /// От этого зависит, можно ли выключить какую-то из записей ленты, не трогая идущие
    /// после неё (<see cref="Services.HistoryManager.CanToggle"/>). Штрих подмешивает свой
    /// битмап поверх, заливка пересчитывается от текущего состояния слоя, стирание
    /// закрашивает свою форму - все трое СКЛАДЫВАЮТСЯ, и пропустить кого-то до них можно.
    /// А прижатие объекта, поворот, кадрирование, смена размера и сборка слоёв пишут
    /// абсолютный снимок: при пересборке они положат его поверх вместе с тем, что
    /// пользователь только что выключил, и выключатель начал бы врать.
    ///
    /// По умолчанию «да»: команда, о которой никто не подумал, автоматически оказывается
    /// невыключаемой. Ошибка в эту сторону стоит отказа, в обратную - совравшей ленты.
    /// </summary>
    bool WritesSnapshot => true;

    /// <summary>
    /// Забыть снимок «до», снятый при первой записи, и снять его заново на следующем
    /// <see cref="Execute"/>.
    ///
    /// Нужно после того, как из ленты выключили правку, лежащую ВЫШЕ этой: прежний снимок
    /// описывает слой вместе с той правкой, и первый же Ctrl+Z вернул бы пиксели по
    /// устаревшему. Умеют это те, кто снимок кеширует; заливка пересчитывает себя сама и
    /// так, а тем, кто пишет снимок, это и не понадобится - выключать что-либо выше них
    /// лента не даёт.
    /// </summary>
    void ForgetBefore() { }
}
