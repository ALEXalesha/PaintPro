using System.IO;
using Microsoft.Win32;
using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// File I/O: open / save common raster formats via SkiaSharp. PNG / JPG / BMP / WEBP.
/// Wraps Win32 file dialogs.
/// </summary>
public sealed class FileService
{
    public string? LastSavedPath { get; private set; }
    public string? LastOpenedPath { get; private set; }

    private const string AllFilter =
        "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|" +
        "PNG|*.png|JPEG|*.jpg;*.jpeg|Bitmap|*.bmp|WebP|*.webp|GIF|*.gif";

    /// <summary>Show an Open dialog and load the picked image. Returns null on cancel.</summary>
    public SKBitmap? OpenImageDialog()
    {
        var dlg = new OpenFileDialog { Filter = AllFilter };
        if (dlg.ShowDialog() != true) return null;
        var bmp = SKBitmap.Decode(dlg.FileName);
        if (bmp is null) return null;
        LastOpenedPath = dlg.FileName;
        return bmp;
    }

    /// <summary>Load <paramref name="path"/> into a bitmap (no dialog).</summary>
    public SKBitmap? OpenImage(string path)
    {
        var bmp = SKBitmap.Decode(path);
        if (bmp is null) return null;
        LastOpenedPath = path;
        return bmp;
    }

    /// <summary>Show a Save As dialog and write the document. Returns true on success.</summary>
    public bool SaveAsDialog(Document doc)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PNG|*.png|JPEG|*.jpg|Bitmap|*.bmp|WebP|*.webp",
            DefaultExt = ".png",
            FileName = "Untitled.png",
        };
        if (dlg.ShowDialog() != true) return false;
        var ok = WriteToFile(doc, dlg.FileName);
        if (ok) LastSavedPath = dlg.FileName;
        return ok;
    }

    /// <summary>Save to the last-opened/saved path if known; otherwise prompts via dialog.</summary>
    public bool SaveOrSaveAs(Document doc)
    {
        var path = LastSavedPath ?? LastOpenedPath;
        if (path is null) return SaveAsDialog(doc);
        return WriteToFile(doc, path);
    }

    private static bool WriteToFile(Document doc, string path)
    {
        using var flat = Flatten(doc);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        SKEncodedImageFormat fmt = ext switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".bmp"  => SKEncodedImageFormat.Bmp,
            ".webp" => SKEncodedImageFormat.Webp,
            ".gif"  => SKEncodedImageFormat.Gif,
            _       => SKEncodedImageFormat.Png,
        };
        using var img = SKImage.FromBitmap(flat);
        using var data = img.Encode(fmt, fmt == SKEncodedImageFormat.Jpeg ? 92 : 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
        return true;
    }

    /// <summary>Flatten all visible layers (and any floating pickup) into a single bitmap.</summary>
    public static SKBitmap Flatten(Document doc)
    {
        var bmp = new SKBitmap(doc.CanvasWidth, doc.CanvasHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        foreach (var layer in doc.Layers) layer.Render(canvas);

        if (doc.FloatingPickup is { } fp)
        {
            canvas.Save();
            if (fp.Rotation != 0)
            {
                var c = fp.Center;
                canvas.Translate(c.X, c.Y);
                canvas.RotateRadians(fp.Rotation);
                canvas.Translate(-c.X, -c.Y);
            }
            if (fp.Quad is { } quad)
            {
                using var path = new SKPath();
                path.MoveTo(quad[0]); path.LineTo(quad[1]); path.LineTo(quad[2]); path.LineTo(quad[3]); path.Close();
                canvas.ClipPath(path, antialias: true);
            }
            canvas.DrawBitmap(fp.SourceBitmap, fp.CurrentBBox);
            canvas.Restore();
        }
        return bmp;
    }
}
