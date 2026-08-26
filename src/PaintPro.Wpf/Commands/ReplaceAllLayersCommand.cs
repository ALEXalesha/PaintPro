using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Swap the pixel content of every layer at once and resize the canvas with it.
/// This is what rotate / flip / crop need: they change the document, not one layer, and
/// the previous per-layer version left the other layers at the old size while the canvas
/// reported the new one.
///
/// Whole snapshots are unavoidable here — these ops touch every pixel, so a diff would be
/// the whole layer anyway. Layer identity, name, visibility and opacity are preserved so
/// history commands recorded against those layers keep resolving.
/// </summary>
public sealed class ReplaceAllLayersCommand : IDocumentCommand, IDisposable
{
    /// <summary>Имя, видимость и прозрачность одного слоя - всё, что у слоя есть кроме пикселей.</summary>
    public readonly record struct LayerProps(string Name, bool Visible, float Opacity);

    private readonly SKBitmap[] _before;
    private readonly int _beforeW, _beforeH;
    private readonly LayerProps[]? _beforeProps;
    private readonly SKBitmap[] _after;
    private readonly int _afterW, _afterH;
    private readonly LayerProps[]? _afterProps;

    /// <param name="before">One snapshot per layer, in layer order (command takes ownership).</param>
    /// <param name="after">Transformed content per layer, same order (command takes ownership).</param>
    /// <param name="beforeProps">
    /// Свойства слоёв, к которым возвращает отмена, или null - «оставить как есть».
    /// </param>
    /// <param name="afterProps">
    /// Свойства слоёв после операции, или null - «оставить как есть». Нужны открытию
    /// файла: поворот и отражение свойств не трогают, а открытие обязано их выправить.
    /// </param>
    public ReplaceAllLayersCommand(string displayName,
        SKBitmap[] before, int beforeW, int beforeH,
        SKBitmap[] after, int afterW, int afterH,
        LayerProps[]? beforeProps = null, LayerProps[]? afterProps = null)
    {
        DisplayName = displayName;
        _before = before; _beforeW = beforeW; _beforeH = beforeH; _beforeProps = beforeProps;
        _after = after; _afterW = afterW; _afterH = afterH; _afterProps = afterProps;
        ChangedAnything = Differs();
    }

    /// <summary>Отличается ли «после» от «до» хоть чем-нибудь.</summary>
    private bool Differs()
    {
        if (_beforeW != _afterW || _beforeH != _afterH) return true;
        if (_before.Length != _after.Length) return true;
        for (int i = 0; i < _before.Length; i++)
            if (!Models.Document.SamePixels(_before[i], _after[i])) return true;
        if (_beforeProps is null != (_afterProps is null)) return true;
        if (_beforeProps is { } bp && _afterProps is { } ap)
        {
            if (bp.Length != ap.Length) return true;
            for (int i = 0; i < bp.Length; i++) if (!bp[i].Equals(ap[i])) return true;
        }
        return false;
    }

    public string DisplayName { get; }

    /// <summary>
    /// Изменила ли операция хоть что-нибудь: размер холста, пиксели слоёв или их свойства.
    ///
    /// Поворот квадратного чистого листа, отражение симметричной картинки, кадрирование по
    /// всему холсту, открытие файла, который на холсте и так лежит, - каждый из них
    /// оставлял в ленте строку и объявлял документ изменённым, при том что на экране не
    /// менялось ничего. То же правило, по которому отсеивают свою пустую работу штрих
    /// (<see cref="DrawStrokeCommand.ChangedAnything"/>), заливка и стирание.
    ///
    /// Свойства слоёв считаются наравне с пикселями: открытие файла в документ со скрытой
    /// бумагой пикселей не меняет, а видимость - меняет, и такую запись пропускать нельзя.
    /// </summary>
    public bool ChangedAnything { get; }

    // The heaviest command in the app: two full copies of every layer.
    public long ApproximateBytes => Total(_before) + Total(_after);

    private static long Total(SKBitmap[] set)
    {
        long sum = 0;
        foreach (var b in set) sum += (long)b.RowBytes * b.Height;
        return sum;
    }

    public void Dispose()
    {
        foreach (var b in _before) b.Dispose();
        foreach (var b in _after) b.Dispose();
    }

    public void Execute(Document doc) => Install(doc, _after, _afterW, _afterH, _afterProps);
    public void Undo(Document doc) => Install(doc, _before, _beforeW, _beforeH, _beforeProps);

    private static void Install(Document doc, SKBitmap[] content, int w, int h, LayerProps[]? props)
    {
        if (content.Length != doc.Layers.Count) return;

        for (int i = 0; i < doc.Layers.Count; i++)
        {
            if (doc.Layers[i] is not PixelLayer cur) continue;
            var p = props is not null && i < props.Length
                ? props[i]
                : new LayerProps(cur.Name, cur.Visible, cur.Opacity);
            var layer = new PixelLayer(w, h)
            {
                Id = cur.Id,
                Name = p.Name,
                Visible = p.Visible,
                Opacity = p.Opacity,
            };
            using (var c = new SKCanvas(layer.Bitmap)) c.DrawBitmap(content[i], 0, 0);
            cur.Dispose();
            doc.Layers[i] = layer;
        }

        doc.CanvasWidth = w;
        doc.CanvasHeight = h;
        // Поворот, отражение и кадрирование переставляют пиксели под выделением, а
        // поворот ещё и меняет размер холста. Рамка на старых координатах указывает уже
        // не на то, что пользователь выделял. Снимаем здесь, чтобы это работало и при
        // откате по истории. В Electron-версии то же самое сделано в 1.4.0.
        //
        // Поднятый объект - по той же причине и тем же порядком. Слои здесь заменяются
        // целиком, и объект, переживший замену, висел над холстом, которого уже нет: его
        // SourceLayerId указывал на слой, чей битмап только что выброшен, а координаты - на
        // прежний размер. Прижать его вправе только тот, кто затевает операцию, и до неё
        // (см. CropTool, MainViewModel.RotateActiveLayer); здесь остаётся только снять.
        doc.DropFloating();
        doc.Selection = null;
    }
}
