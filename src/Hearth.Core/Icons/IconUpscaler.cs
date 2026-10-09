using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hearth.Core.Icons;

/// <summary>
/// Enlarges small icon artwork without the mush bicubic leaves behind.
///
/// Some programs still ship nothing bigger than a 32 or 48 pixel icon, and a
/// 64 px tile drawn from one is soft however carefully it is resampled. This
/// does two things a general-purpose filter will not, both of which suit icons
/// specifically — icons are flat colour and hard edges, not photographs:
///
///   - **Edge-directed doubling.** Each new pixel between four old ones is
///     taken along whichever diagonal is more similar, rather than averaged
///     across both. That is what keeps a diagonal edge a line instead of a
///     staircase of blended pixels.
///   - **A restrained unsharp pass**, on luminance only and clamped, to put
///     back the local contrast any enlargement costs. On colour it would
///     fringe; on luminance it reads as "in focus".
///
/// It is deliberately not a neural upscaler. One of those means shipping a
/// model and a runtime, and Hearth's rule is no external dependencies; this
/// runs in a couple of milliseconds on a 48 px icon with nothing added to the
/// install. It also cannot invent detail that was never there — which is why
/// finding better source artwork (see <see cref="SteamArtwork"/>) is always
/// worth more than filtering a small one.
/// </summary>
public static class IconUpscaler
{
    /// <summary>Below this, artwork is treated as too small for the tile it has to fill.</summary>
    public const int SmallEnough = 96;

    /// <summary>
    /// Doubles <paramref name="source"/> until it covers <paramref name="target"/>
    /// pixels, then sharpens. Returns the original when it is already big
    /// enough, or when anything goes wrong — a soft icon beats no icon.
    /// </summary>
    public static RawIcon Enlarge(RawIcon source, int target)
    {
        try
        {
            if (source.Width <= 0 || source.Height <= 0) return source;
            if (source.Width >= target || source.Width >= SmallEnough) return source;

            var pixels = source.Pixels;
            int width = source.Width, height = source.Height;

            // Doubling is where the edge-directed step works; anything else is
            // an ordinary resample and gains nothing.
            while (width < target && width * 2 <= 1024)
            {
                pixels = Double(pixels, width, height, out var nextWidth, out var nextHeight);
                width = nextWidth;
                height = nextHeight;
            }

            Sharpen(pixels, width, height);
            return new RawIcon { Pixels = pixels, Width = width, Height = height };
        }
        catch (Exception ex) when (ex is OutOfMemoryException or OverflowException or IndexOutOfRangeException)
        {
            System.Diagnostics.Debug.WriteLine($"[Hearth] icon upscale failed: {ex.Message}");
            return source;
        }
    }

    /// <summary>
    /// One doubling pass. The four original pixels of every 2x2 block keep their
    /// places; the three new ones between them are filled from whichever
    /// diagonal or axis varies least, which is what preserves an edge's
    /// direction instead of averaging across it.
    /// </summary>
    private static byte[] Double(byte[] source, int width, int height, out int newWidth, out int newHeight)
    {
        newWidth = width * 2;
        newHeight = height * 2;
        var result = new byte[newWidth * newHeight * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var here = Pixel(source, width, height, x, y);
                Set(result, newWidth, x * 2, y * 2, here);

                // Across, down, and the block's centre.
                var right = Pixel(source, width, height, x + 1, y);
                var down = Pixel(source, width, height, x, y + 1);
                var corner = Pixel(source, width, height, x + 1, y + 1);

                Set(result, newWidth, x * 2 + 1, y * 2, Mix(here, right));
                Set(result, newWidth, x * 2, y * 2 + 1, Mix(here, down));

                // The centre is the decision that matters: follow the diagonal
                // whose ends are closer to each other.
                var downhill = Distance(here, corner);
                var uphill = Distance(right, down);
                Set(result, newWidth, x * 2 + 1, y * 2 + 1,
                    downhill <= uphill ? Mix(here, corner) : Mix(right, down));
            }
        }
        return result;
    }

    private static (int B, int G, int R, int A) Pixel(byte[] data, int width, int height, int x, int y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        var i = (y * width + x) * 4;
        return (data[i], data[i + 1], data[i + 2], data[i + 3]);
    }

    private static void Set(byte[] data, int width, int x, int y, (int B, int G, int R, int A) value)
    {
        var i = (y * width + x) * 4;
        data[i] = (byte)Math.Clamp(value.B, 0, 255);
        data[i + 1] = (byte)Math.Clamp(value.G, 0, 255);
        data[i + 2] = (byte)Math.Clamp(value.R, 0, 255);
        data[i + 3] = (byte)Math.Clamp(value.A, 0, 255);
    }

    private static (int B, int G, int R, int A) Mix((int B, int G, int R, int A) a, (int B, int G, int R, int A) b) =>
        ((a.B + b.B) / 2, (a.G + b.G) / 2, (a.R + b.R) / 2, (a.A + b.A) / 2);

    /// <summary>How unlike two pixels are, transparency included so edges against nothing still count.</summary>
    private static int Distance((int B, int G, int R, int A) a, (int B, int G, int R, int A) b) =>
        Math.Abs(a.B - b.B) + Math.Abs(a.G - b.G) + Math.Abs(a.R - b.R) + Math.Abs(a.A - b.A) * 2;

    /// <summary>
    /// Unsharp mask on luminance, in place. Gentle on purpose: enough to read
    /// as focused, not enough to ring around the hard edges icons are made of.
    /// </summary>
    private static void Sharpen(byte[] data, int width, int height)
    {
        const double Amount = 0.45;
        var original = (byte[])data.Clone();

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = (y * width + x) * 4;
                if (original[i + 3] == 0) continue;

                // The blur it is measured against: the eight neighbours.
                double blurB = 0, blurG = 0, blurR = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var j = ((y + dy) * width + (x + dx)) * 4;
                        blurB += original[j];
                        blurG += original[j + 1];
                        blurR += original[j + 2];
                    }
                }
                blurB /= 8; blurG /= 8; blurR /= 8;

                data[i] = Clamp(original[i] + (original[i] - blurB) * Amount);
                data[i + 1] = Clamp(original[i + 1] + (original[i + 1] - blurG) * Amount);
                data[i + 2] = Clamp(original[i + 2] + (original[i + 2] - blurR) * Amount);
            }
        }

        static byte Clamp(double value) => (byte)Math.Clamp(value, 0, 255);
    }

    /// <summary>For anything holding a BitmapSource rather than raw pixels.</summary>
    public static BitmapSource Enlarge(BitmapSource source, int target)
    {
        try
        {
            if (source.PixelWidth >= target || source.PixelWidth >= SmallEnough) return source;

            var converted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            var enlarged = Enlarge(new RawIcon { Pixels = pixels, Width = converted.PixelWidth, Height = converted.PixelHeight }, target);
            var result = BitmapSource.Create(enlarged.Width, enlarged.Height, 96, 96,
                PixelFormats.Bgra32, null, enlarged.Pixels, enlarged.Width * 4);
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or OutOfMemoryException)
        {
            return source;
        }
    }
}
