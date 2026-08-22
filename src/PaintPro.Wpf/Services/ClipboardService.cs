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
    /// The selection rect inside <paramref name="doc"/>, or the full canvas if there is no
    /// selection — flattened. Copy takes what the user can see; reading the active layer
    /// alone put an unexpectedly empty or partial image on the clipboard as soon as the
    /// document had more than one layer.
    /// </summary>
    private static SKBitmap ExtractSelectedRegion(Document doc)
    {
        var canvasRect = new SKRectI(0, 0, doc.CanvasWidth, doc.CanvasHeight);
        // Плавающий объект по инварианту обнуляет Selection, поэтому ветка "нет
        // выделения - копируем весь холст" срабатывала сразу после того, как
        // выделение подняли для перемещения: Ctrl+C по перетащенной картинке
        // клал в буфер обмена весь документ.
        SKRectI rect = doc.Selection switch
        {
            RectSelection rs => SKRectI.Round(rs.Rect),
            PolygonSelection poly => SKRectI.Round(poly.BoundingBox),
            _ when doc.FloatingPickup is { } fp
                => Document.PickupBounds(fp, doc.CanvasWidth, doc.CanvasHeight),
            _ => canvasRect,
        };
        rect = SKRectI.Intersect(rect, canvasRect);
        if (rect.IsEmpty) return new SKBitmap(1, 1);

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
    /// False - буфер обмена так и не отдался.
    /// </summary>
    public bool Copy(Document doc)
    {
        using var bmp = ExtractSelectedRegion(doc);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        // Кодировщик возвращает null, а не бросает: без проверки обычный Ctrl+C падал бы
        // с NullReferenceException до глобального обработчика и пугал окном «Что-то
        // пошло не так» - при том, что копирование это просто не удалось.
        if (data is null) return false;
        using var ms = new MemoryStream(data.ToArray());
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return Retry(() => Clipboard.SetImage(bi));
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
