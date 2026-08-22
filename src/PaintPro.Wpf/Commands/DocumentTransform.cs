using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Commands;

/// <summary>
/// Builds whole-document transforms (rotate / flip / crop) as a single
/// <see cref="ReplaceAllLayersCommand"/>.
///
/// Every layer goes through the same transform, which is the whole point: doing it to the
/// active layer alone leaves the rest at the old size while the canvas already reports the
/// new one, and the document renders clipped and misaligned from then on.
/// </summary>
public static class DocumentTransform
{
    public static ReplaceAllLayersCommand Rotate(Document doc, float radians)
    {
        bool quarter = MathF.Abs(MathF.Abs(radians) - MathF.PI / 2f) < 0.01f;
        int oldW = doc.CanvasWidth, oldH = doc.CanvasHeight;
        int newW = quarter ? oldH : oldW;
        int newH = quarter ? oldW : oldH;
        var label = radians > 0 ? "Rotate CW" : "Rotate CCW";

        return Build(doc, label, newW, newH, (canvas, _) =>
        {
            canvas.Translate(newW / 2f, newH / 2f);
            canvas.RotateRadians(radians);
            canvas.Translate(-oldW / 2f, -oldH / 2f);
        });
    }

    public static ReplaceAllLayersCommand Flip(Document doc, bool horizontal)
    {
        int w = doc.CanvasWidth, h = doc.CanvasHeight;
        var label = horizontal ? "Flip horizontal" : "Flip vertical";

        return Build(doc, label, w, h, (canvas, _) =>
        {
            if (horizontal) { canvas.Translate(w, 0); canvas.Scale(-1, 1); }
            else            { canvas.Translate(0, h); canvas.Scale(1, -1); }
        });
    }

    public static ReplaceAllLayersCommand Crop(Document doc, SKRectI region)
        => Build(doc, "Crop", region.Width, region.Height,
                 (canvas, _) => canvas.Translate(-region.Left, -region.Top));

    /// <summary>
    /// Заменить документ содержимым открытого файла: холст под размер картинки, нижний
    /// слой - сама картинка на белом, верхние - пустые.
    ///
    /// Не то же самое, что «изменить размер и нарисовать в активный слой»: смена размера
    /// переносит содержимое ВСЕХ слоёв, и старый рисунок с верхних продолжал лежать
    /// поверх открытой фотографии. Видно его было сразу, а попадал он ещё и в файл -
    /// <see cref="Services.FileService.Flatten"/> складывает все слои.
    /// </summary>
    public static ReplaceAllLayersCommand OpenImage(Document doc, SKBitmap image)
    {
        int count = doc.Layers.Count;
        var before = new SKBitmap[count];
        var after = new SKBitmap[count];
        int w = image.Width, h = image.Height;

        for (int i = 0; i < count; i++)
        {
            before[i] = doc.Layers[i] is PixelLayer pl
                ? pl.ExtractRegion(new SKRectI(0, 0, pl.Width, pl.Height))
                : new SKBitmap(1, 1);

            var dst = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(dst))
            {
                if (i == 0)
                {
                    c.Clear(SKColors.White);
                    c.DrawBitmap(image, 0, 0);
                }
                else
                {
                    c.Clear(SKColors.Transparent);
                }
            }
            after[i] = dst;
        }

        return new ReplaceAllLayersCommand("Открытие",
            before, doc.CanvasWidth, doc.CanvasHeight,
            after, w, h);
    }

    /// <summary>
    /// Snapshot every layer, render each one through <paramref name="setTransform"/> into a
    /// new bitmap of (newW, newH), and wrap both sides in an undoable command.
    /// The bottom layer keeps its opaque background; layers above stay transparent where
    /// the transform leaves them empty.
    /// </summary>
    private static ReplaceAllLayersCommand Build(
        Document doc, string label, int newW, int newH, Action<SKCanvas, int> setTransform)
    {
        int count = doc.Layers.Count;
        var before = new SKBitmap[count];
        var after = new SKBitmap[count];

        for (int i = 0; i < count; i++)
        {
            if (doc.Layers[i] is not PixelLayer pl)
            {
                before[i] = new SKBitmap(1, 1);
                after[i] = new SKBitmap(1, 1);
                continue;
            }

            before[i] = pl.ExtractRegion(new SKRectI(0, 0, pl.Width, pl.Height));

            var dst = new SKBitmap(newW, newH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(dst))
            {
                if (i == 0) c.Clear(SKColors.White);
                else c.Clear(SKColors.Transparent);
                setTransform(c, i);
                c.DrawBitmap(pl.Bitmap, 0, 0);
            }
            after[i] = dst;
        }

        return new ReplaceAllLayersCommand(label,
            before, doc.CanvasWidth, doc.CanvasHeight,
            after, newW, newH);
    }
}
