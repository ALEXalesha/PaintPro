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
    /// Called when the tool is being deactivated (user switched). The tool MUST drop its
    /// own half-finished gesture here — a stroke that never got its PointerUp, a drag flag
    /// left set — otherwise it leaks into the next activation.
    ///
    /// Поднятый объект инструмент не трогает: он принадлежит документу, а не тому, кто им
    /// сейчас двигает, и переход «Выделение» ↔ «Четырёхугольник» его не заканчивает.
    /// Прижимает его <see cref="ViewModels.MainViewModel.OnActiveToolChanged"/> - только
    /// там известно, на какой инструмент меняют.
    /// </summary>
    void OnDeactivate(ToolContext ctx);

    /// <summary>
    /// Optional preview bitmap drawn on top of the canvas (e.g. shape rubber-band,
    /// brush ghost). Returns null if no live preview is in progress.
    /// </summary>
    SKBitmap? PreviewBitmap { get; }

    /// <summary>
    /// Alpha the preview should be composited at. Tools that render their stroke opaque
    /// and apply transparency at merge time report it here so the live preview matches
    /// what lands on the layer.
    /// </summary>
    byte PreviewAlpha => 255;

    /// <summary>
    /// Режим, которым превью сольётся со слоем. По умолчанию обычный source-over: чем
    /// инструмент рисует, тем он и покажет. Ластик на верхнем слое пиксели вычитает, и
    /// показывать белую полосу вместо дыры значит показывать не то, что получится.
    /// </summary>
    SKBlendMode PreviewBlendMode => SKBlendMode.SrcOver;

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

    /// <summary>
    /// Текущий масштаб (экранных пикселей на пиксель документа). Инструментам он нужен там,
    /// где размер задан на экране, а сравнение идёт в координатах документа: зона хвата
    /// ручки нарисована в экранных пикселях и обязана оставаться такой на любом масштабе.
    /// Пока хват мерился в пикселях документа, на уменьшенной картинке в него нельзя было
    /// попасть вовсе, а на увеличенной он накрывал пол-экрана и перехватывал перетаскивание
    /// самого объекта.
    /// </summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>Перевести экранное расстояние в координаты документа.</summary>
    public float ToDocument(float screenPixels)
        => (float)(screenPixels / Math.Max(Zoom, 0.01));

    /// <summary>
    /// Куда инструмент отдаёт короткое объяснение, почему жест ничего не сделал.
    /// Показывает статусбар; хост подписывается сам, тулы про UI не знают.
    /// </summary>
    public Action<string>? ReportHint { get; set; }

    /// <summary>
    /// Слой, в который инструменты сейчас пишут, или null, если писать некуда.
    ///
    /// Проверка на видимость нужна здесь, а не в каждом инструменте: штрих по скрытому
    /// слою уходил в его битмап целиком - на экране не появлялось ничего, зато в истории
    /// появлялась запись, а в файл нарисованное попадало, стоило слой включить.
    /// Пользователь при этом видел, что кисть не работает, и без объяснений.
    ///
    /// Слой, у которого прозрачность выкручена в ноль, ничем от скрытого не отличается:
    /// ползунок в панели слоёв начинается с нуля, довести его туда - одно движение, и
    /// дальше повторялась ровно та же история. На экране не появлялось ничего, в файл
    /// (<see cref="Services.FileService.Flatten"/> умножает на прозрачность) - тоже, а
    /// запись в историю появлялась, и приложение начинало спрашивать про сохранение.
    /// </summary>
    public PixelLayer? DrawTarget()
    {
        if (Document.ActiveLayer is not PixelLayer pl) return null;
        if (!pl.Visible)
        {
            ReportHint?.Invoke($"Слой «{pl.Name}» скрыт — включите видимость, чтобы рисовать");
            return null;
        }
        if (pl.Opacity <= 0f)
        {
            ReportHint?.Invoke($"Слой «{pl.Name}» полностью прозрачен — поднимите его прозрачность");
            return null;
        }
        return pl;
    }

    /// <summary>
    /// Сказать, что чернил в инструменте нет: прозрачность выкручена в ноль.
    ///
    /// Такой штрих не меняет ни одного пикселя и в историю не попадает (это правило
    /// живёт с 1.15.0), но пользователю об этом не говорилось ни слова: он водил мышью
    /// по холсту, на холсте не появлялось ничего, и отличить это от сломанной программы
    /// было нельзя. Ровно та же беда была со скрытым слоем, и решается она тем же -
    /// строкой в статусбаре. Ползунок прозрачности начинается с нуля, так что попасть
    /// сюда можно одним движением.
    /// </summary>
    public void ReportTransparentInk()
        => ReportHint?.Invoke("Прозрачность выставлена в ноль — на холсте не останется ничего");

    public ToolContext(Document doc) => Document = doc;
}
