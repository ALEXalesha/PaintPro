using System.IO;
using Microsoft.Win32;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Services;

public enum SaveStatus
{
    Ok,
    /// <summary>The requested container can't be written, so a PNG was written instead.</summary>
    FormatChanged,
    Cancelled,
    Failed,
}

/// <param name="Path">Where the file actually landed (may differ from the requested path).</param>
public readonly record struct SaveOutcome(SaveStatus Status, string? Path = null, string? Error = null);

public enum OpenStatus
{
    Ok,
    Cancelled,
    /// <summary>Файл не читается или это не картинка.</summary>
    Failed,
}

/// <param name="Bitmap">Декодированная картинка; владение переходит вызывающему.</param>
public readonly record struct OpenOutcome(
    OpenStatus Status, SKBitmap? Bitmap = null, string? Path = null, string? Error = null);

/// <summary>
/// File I/O: open / save raster formats via SkiaSharp.
///
/// Skia can only ENCODE png / jpeg / webp — Encode() hands back null for bmp and gif.
/// Anything else is therefore written as PNG under a .png name rather than producing an
/// empty file or a null dereference.
/// </summary>
public sealed class FileService
{
    public string? LastSavedPath { get; private set; }
    public string? LastOpenedPath { get; private set; }

    private const string OpenFilter =
        "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|" +
        "PNG|*.png|JPEG|*.jpg;*.jpeg|Bitmap|*.bmp|WebP|*.webp|GIF|*.gif";

    /// <summary>Only the containers Skia can actually encode are offered when saving.</summary>
    private const string SaveFilter = "PNG|*.png|JPEG|*.jpg;*.jpeg|WebP|*.webp";

    /// <summary>Show an Open dialog and load the picked image.</summary>
    public OpenOutcome OpenImageDialog()
    {
        var dlg = new OpenFileDialog { Filter = OpenFilter };
        if (dlg.ShowDialog() != true) return new OpenOutcome(OpenStatus.Cancelled);
        return OpenImage(dlg.FileName);
    }

    /// <summary>
    /// Load <paramref name="path"/> into a bitmap (no dialog).
    ///
    /// Отказ отличается от отмены: <see cref="SKBitmap.Decode(string)"/> возвращает null и на
    /// удалённый файл, и на папку, и на битые байты. Пока оба случая были одним null,
    /// «Открыть» на нечитаемом файле не делало ровно ничего и молчало. В Electron-версии это
    /// починено в 1.8.0.
    /// </summary>
    public OpenOutcome OpenImage(string path)
    {
        SKBitmap? bmp;
        try
        {
            // Габарит читаем ИЗ ЗАГОЛОВКА, до раскодирования. Диалог «Изменить размер
            // холста» держит потолок в 20000 по стороне и 120 млн пикселей всего, а
            // «Открыть» не проверяло ничего: снимок 25000x25000 - это 2,5 ГБ на слой,
            // и раскодирование падало нехваткой памяти мимо всех catch'ей, унося
            // несохранённый рисунок вместе с приложением. Отказ теперь виден и приходит
            // раньше, чем под картинку выделят хоть байт.
            if (!IsOpenable(path, out int w, out int h))
            {
                return new OpenOutcome(OpenStatus.Failed, null, path,
                    $"Картинка {w}×{h} слишком велика: сторона не больше " +
                    $"{Commands.ResizeCanvasCommand.MaxDimension} пикселей и не больше " +
                    $"{Commands.ResizeCanvasCommand.MaxPixels / 1_000_000} млн пикселей всего.");
            }
            bmp = SKBitmap.Decode(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new OpenOutcome(OpenStatus.Failed, null, path, ex.Message);
        }
        if (bmp is null)
            return new OpenOutcome(OpenStatus.Failed, null, path, "Файл повреждён или это не изображение.");
        LastOpenedPath = path;
        // Только что открытый файл и есть цель следующего Ctrl+S. Без сброса
        // SaveOrSaveAs брал LastSavedPath - то есть картинку, сохранённую до
        // открытия, - и молча записывал в неё содержимое нового документа.
        LastSavedPath = null;
        return new OpenOutcome(OpenStatus.Ok, bmp, path);
    }

    /// <summary>
    /// Влезет ли картинка по этому пути в документ. Габарит берётся из заголовка файла
    /// (<see cref="SKCodec"/>), то есть без раскодирования: в этом весь смысл проверки.
    ///
    /// Заголовок не прочитался - пусть решает <see cref="SKBitmap.Decode(string)"/>: он
    /// отличает битый файл от нечитаемого и говорит об этом своими словами.
    /// </summary>
    public static bool IsOpenable(string path, out int width, out int height)
    {
        width = height = 0;
        using var codec = SKCodec.Create(path);
        if (codec is null) return true;
        width = codec.Info.Width;
        height = codec.Info.Height;
        return Commands.ResizeCanvasCommand.IsAllowed(width, height);
    }

    /// <summary>Отвязать документ от файла: следующий Ctrl+S спросит путь заново.</summary>
    public void Detach()
    {
        LastSavedPath = null;
        LastOpenedPath = null;
    }

    /// <summary>Show a Save As dialog and write the document.</summary>
    public SaveOutcome SaveAsDialog(Document doc)
    {
        var dlg = new SaveFileDialog
        {
            Filter = SaveFilter,
            DefaultExt = ".png",
            FileName = Path.GetFileNameWithoutExtension(LastSavedPath ?? LastOpenedPath ?? "Untitled") + ".png",
        };
        if (dlg.ShowDialog() != true) return new SaveOutcome(SaveStatus.Cancelled);
        var outcome = WriteToFile(doc, dlg.FileName);
        if (outcome.Status is SaveStatus.Ok or SaveStatus.FormatChanged) LastSavedPath = outcome.Path;
        return outcome;
    }

    /// <summary>
    /// Save to the current target (last saved, else last opened) if there is one;
    /// otherwise prompt via dialog. An opened file stays the Ctrl+S target — but if its
    /// container can't be encoded, the write lands next to it as PNG instead of failing.
    /// </summary>
    public SaveOutcome SaveOrSaveAs(Document doc)
    {
        var target = LastSavedPath ?? LastOpenedPath;
        if (target is null) return SaveAsDialog(doc);
        var outcome = WriteToFile(doc, target);
        if (outcome.Status is SaveStatus.Ok or SaveStatus.FormatChanged) LastSavedPath = outcome.Path;
        return outcome;
    }

    /// <summary>Pick the encoder for a path, falling back to PNG (and a .png name) when it can't be written.</summary>
    private static (SKEncodedImageFormat Format, string Path, bool Changed) ResolveTarget(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => (SKEncodedImageFormat.Jpeg, path, false),
            ".webp"           => (SKEncodedImageFormat.Webp, path, false),
            ".png"            => (SKEncodedImageFormat.Png, path, false),
            _                 => (SKEncodedImageFormat.Png, Path.ChangeExtension(path, ".png"), true),
        };

    private static SaveOutcome WriteToFile(Document doc, string requestedPath)
    {
        var (fmt, path, changed) = ResolveTarget(requestedPath);
        try
        {
            using var flat = Flatten(doc);
            using var img = SKImage.FromBitmap(flat);
            using var data = img.Encode(fmt, fmt == SKEncodedImageFormat.Jpeg ? 92 : 100);
            if (data is null)
                return new SaveOutcome(SaveStatus.Failed, path, "Кодировщик вернул пустой результат.");

            // Create, not OpenWrite: OpenWrite keeps the old file's length, so writing a
            // smaller image over a bigger one leaves the tail of the previous file behind.
            using var stream = File.Create(path);
            data.SaveTo(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SaveOutcome(SaveStatus.Failed, path, ex.Message);
        }
        return new SaveOutcome(changed ? SaveStatus.FormatChanged : SaveStatus.Ok, path);
    }

    /// <summary>
    /// Flatten all visible layers (and any floating pickup) into a single bitmap.
    /// Сборка - общая с экраном (<see cref="Document.Render"/>): файл обязан совпадать с
    /// тем, что видит пользователь, вплоть до того, на каком слое лежит поднятый объект.
    /// </summary>
    public static SKBitmap Flatten(Document doc)
    {
        var bmp = new SKBitmap(doc.CanvasWidth, doc.CanvasHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            doc.Render(canvas);
        }
        return bmp;
    }
}
