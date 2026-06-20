using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// Helpers for turning a flat background colour into transparency.
///
/// The document's background layer is opaque white, so a rectangular selection
/// always captures the white around the drawn marks. When that selection is lifted
/// into a <see cref="Models.FloatingPickup"/> and moved, the white travels with it and
/// paints over whatever is underneath. Keying the background out fixes that: only the
/// drawn pixels remain opaque, the rest becomes transparent and composites cleanly.
/// </summary>
public static class BitmapKeying
{
    /// <summary>
    /// Return a copy of <paramref name="source"/> where every pixel within
    /// <paramref name="tolerance"/> of <paramref name="background"/> (per channel) is made
    /// fully transparent. The original bitmap is left untouched.
    /// </summary>
    public static SKBitmap KeyOutBackground(SKBitmap source, SKColor background, int tolerance = 8)
    {
        // Copy into an unpremultiplied bitmap so channel values are the raw colour and
        // writing alpha=0 doesn't leave premultiplied colour residue behind.
        var dst = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(dst))
            canvas.DrawBitmap(source, 0, 0);

        var pixels = dst.Pixels; // SKColor[] copy, unpremultiplied
        byte br = background.Red, bg = background.Green, bb = background.Blue;

        for (int i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            if (p.Alpha == 0) continue; // already transparent
            if (Near(p.Red, br, tolerance) &&
                Near(p.Green, bg, tolerance) &&
                Near(p.Blue, bb, tolerance))
            {
                pixels[i] = SKColors.Transparent;
            }
        }

        dst.Pixels = pixels;
        return dst;
    }

    private static bool Near(byte a, byte b, int tolerance) => Math.Abs(a - b) <= tolerance;
}
