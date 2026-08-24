using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Services;

/// <summary>Чем кончилась попытка прочитать буфер обмена.</summary>
public enum ClipboardStatus
{
    Ok,
    /// <summary>Картинки в буфере нет.</summary>
    Empty,
    /// <summary>Буфер держит другое приложение и не отдаёт.</summary>
    Busy,
}

/// <param name="Bitmap">Декодированная картинка; владение переходит вызывающему.</param>
public readonly record struct ClipboardOutcome(ClipboardStatus Status, SKBitmap? Bitmap = null);

/// <summary>Чем кончилась попытка положить картинку в буфер обмена.</summary>
public enum CopyStatus
{
    Ok,
    /// <summary>Копировать нечего: выделение или объект целиком за пределами холста.</summary>
    Nothing,
    /// <summary>Буфер держит другое приложение и не отдаёт.</summary>
    Busy,
}

/// <summary>
/// System clipboard bridge for raster images. Uses WPF's Clipboard API and converts
/// to/from SkiaSharp via PNG round-trip (the safest cross-app format).
///
/// Буфер обмена в Windows - один на всех и захватывается монопольно: пока его держит
/// чужое приложение, вызов падает с <see cref="COMException"/> (CLIPBRD_E_CANT_OPEN).
/// Это обычное дело, а не сбой, поэтому каждая операция повторяется несколько раз с
/// паузой. Без этого обычный Ctrl+C время от времени доходил до
/// <c>App.DispatcherUnhandledException</c> и пугал пользователя окном «Что-то пошло не так».
/// </summary>
public sealed class ClipboardService
{
    private const int Attempts = 5;
    private const int RetryDelayMs = 60;

    /// <summary>Выполнить операцию с буфером, повторяя, пока его не отпустят. False - не дождались.</summary>
    private static bool Retry(Action operation)
    {
        for (int i = 0; i < Attempts; i++)
        {
            try
            {
                operation();
                return true;
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                if (i < Attempts - 1) Thread.Sleep(RetryDelayMs);
            }
        }
        return false;
    }

    /// <summary>
    /// Что уходит в буфер обмена: поднятый объект, область выделения или весь холст.
    ///
    /// Выделение копируется сборкой всех слоёв: копия берёт то, что пользователь видит, а
    /// чтение одного активного слоя клало в буфер неожиданно пустую или наполовину пустую
    /// картинку, стоило документу обзавестись вторым слоем.
    ///
    /// А вот поднятый объект копируется САМ - на прозрачном фоне и без того, что лежит под
    /// ним. Прежде и он шёл через общую сборку, обрезанную по своему габариту: объект,
    /// уведённый на цветное место, попадал в буфер вместе с этим цветом прямоугольной
    /// заплаткой, и вставка возвращала на холст не фигуру, а плитку фона с фигурой внутри.
    /// Габарит при этом ещё и шире самого объекта - у повёрнутого сильно, - так что фона
    /// в копии было больше, чем содержимого. В Electron-версии Ctrl+C по floating рисует
    /// в чистый холст один только объект (<c>copySelection</c>).
    /// </summary>
    /// <returns>
    /// null - копировать нечего: рамка или объект целиком уехали за холст. Прежде на это
    /// возвращался битмап 1x1, он честно уходил в буфер обмена, и Ctrl+C по объекту,
    /// уведённому за край, заканчивался молча и «успешно»: буфер терял то, что в нём
    /// лежало, а Ctrl+X к тому же выбрасывал сам объект - в обмен на один прозрачный
    /// пиксель.
    /// </returns>
    public static SKBitmap? ExtractForClipboard(Document doc)
    {
        var canvasRect = new SKRectI(0, 0, doc.CanvasWidth, doc.CanvasHeight);

        // Плавающий объект по инварианту обнуляет Selection, поэтому без этой ветки
        // срабатывала бы следующая - «нет выделения, копируем весь холст».
        if (doc.FloatingPickup is { } fp)
        {
            var bounds = Document.PickupBounds(fp, doc.CanvasWidth, doc.CanvasHeight);
            if (!bounds.HasArea()) return null;
            var only = new SKBitmap(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(only))
            {
                c.Clear(SKColors.Transparent);
                // DrawPickup рисует в координатах документа и уважает трансформацию канвы.
                c.Translate(-bounds.Left, -bounds.Top);
                Document.DrawPickup(c, fp);
            }
            return only;
        }

        SKRectI rect = doc.Selection switch
        {
            RectSelection rs => SKRectI.Round(rs.Rect),
            PolygonSelection poly => SKRectI.Round(poly.BoundingBox),
            _ => canvasRect,
        };
        rect = SKRectI.Intersect(rect, canvasRect);
        if (!rect.HasArea()) return null;

        using var flat = FileService.Flatten(doc);
        var dst = new SKBitmap(rect.Width, rect.Height, flat.ColorType, flat.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.DrawBitmap(flat,
            source: new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
            dest: new SKRect(0, 0, rect.Width, rect.Height));
        return dst;
    }

    /// <summary>
    /// Copy the current selection (or whole canvas) to the OS clipboard.
    /// <see cref="CopyStatus.Busy"/> - буфер обмена так и не отдался,
    /// <see cref="CopyStatus.Nothing"/> - копировать было нечего.
    /// </summary>
    public CopyStatus Copy(Document doc)
    {
        using var bmp = ExtractForClipboard(doc);
        if (bmp is null) return CopyStatus.Nothing;
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        // Кодировщик возвращает null, а не бросает: без проверки обычный Ctrl+C падал бы
        // с NullReferenceException до глобального обработчика и пугал окном «Что-то
        // пошло не так» - при том, что копирование это просто не удалось.
        if (data is null) return CopyStatus.Busy;
        using var ms = new MemoryStream(data.ToArray());
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return Retry(() => Clipboard.SetImage(bi)) ? CopyStatus.Ok : CopyStatus.Busy;
    }

    /// <summary>
    /// Try to read a bitmap from the clipboard. <see cref="ClipboardStatus.Empty"/> и
    /// <see cref="ClipboardStatus.Busy"/> - разные вещи: в первом случае вставлять нечего,
    /// во втором есть что, но прочитать не дали, и об этом стоит сказать.
    /// </summary>
    public ClipboardOutcome TryGetImage()
    {
        BitmapSource? src = null;
        bool got = Retry(() =>
        {
            src = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
        });
        if (!got) return new ClipboardOutcome(ClipboardStatus.Busy);
        if (src is null) return new ClipboardOutcome(ClipboardStatus.Empty);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        var decoded = SKBitmap.Decode(ms);
        return decoded is null
            ? new ClipboardOutcome(ClipboardStatus.Empty)
            : new ClipboardOutcome(ClipboardStatus.Ok, decoded);
    }
}
