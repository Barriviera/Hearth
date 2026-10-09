using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Hearth.App.Views.Start;

/// <summary>
/// A blurred picture of whatever is behind the Start menu, captured just
/// before it appears.
///
/// The system backdrops cannot be relied on for this: Mica only ever shows the
/// wallpaper, and both Mica and Acrylic turn into flat grey whenever Windows
/// switches transparency effects off — which Energy Saver does, and this
/// user's machine runs with Energy Saver on. A snapshot does not follow
/// changes behind the menu while it is open, which for a menu that is open
/// for a second or two is not noticeable.
/// </summary>
internal static class StartBackdropCapture
{
    /// <summary>
    /// Captures a screen rectangle (physical pixels) with a margin around it,
    /// so the blur has real pixels to sample at the edges instead of fading
    /// to black. Returns null if the capture fails.
    /// </summary>
    public static BitmapSource? Capture(Int32Rect area, int margin)
    {
        var x = area.X - margin;
        var y = area.Y - margin;
        var width = area.Width + margin * 2;
        var height = area.Height + margin * 2;
        if (width <= 0 || height <= 0) return null;

        // Shrunk by GDI on the way out of the screen DC rather than afterwards.
        // Blitting at full size meant copying about 4.6 MB into a managed
        // bitmap only to throw fifteen sixteenths of it away; one StretchBlt
        // does the whole job into a bitmap a quarter the size on each side.
        // Quality does not matter here because the result is blurred, so the
        // fastest stretch mode is the right one.
        var smallWidth = Math.Max(1, (int)Math.Round(width * Scale));
        var smallHeight = Math.Max(1, (int)Math.Round(height * Scale));

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, smallWidth, smallHeight);
        var previous = SelectObject(memory, bitmap);
        try
        {
            SetStretchBltMode(memory, COLORONCOLOR);
            if (!StretchBlt(memory, 0, 0, smallWidth, smallHeight, screen, x, y, width, height, SRCCOPY | CAPTUREBLT))
                return null;
            SelectObject(memory, previous);

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>How much smaller than the screen the capture is kept. The blur radius follows it.</summary>
    private const double Scale = 0.25;

    /// <summary>
    /// The backdrop element: the capture, blurred, stretched past the window
    /// edges by the margin, under a theme tint.
    /// </summary>
    public static UIElement Build(BitmapSource capture, double marginDips, Brush tint)
    {
        var image = new System.Windows.Controls.Image
        {
            Source = Blurred(capture) ?? capture,
            Stretch = Stretch.Fill,
            Margin = new Thickness(-marginDips),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);

        return new System.Windows.Controls.Grid
        {
            ClipToBounds = true,
            IsHitTestVisible = false,
            Children =
            {
                image,
                new System.Windows.Shapes.Rectangle { Fill = tint },
            },
        };
    }

    /// <summary>
    /// Blurs the capture now, into a frozen bitmap.
    ///
    /// A live BlurEffect is evaluated on the first frame the window presents -
    /// which is precisely the frame the user is waiting for, and on integrated
    /// graphics it is the most expensive thing in it. Doing the blur here puts
    /// the cost before the window appears at all. The capture is already at a
    /// quarter size, so the radius is a quarter of the 48 the effect used in
    /// full-size space.
    /// </summary>
    private static BitmapSource? Blurred(BitmapSource capture)
    {
        try
        {
            var visual = new DrawingVisual
            {
                Effect = new BlurEffect
                {
                    Radius = 48 * Scale,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Performance,
                },
            };
            using (var context = visual.RenderOpen())
                context.DrawImage(capture, new Rect(0, 0, capture.PixelWidth, capture.PixelHeight));

            var target = new RenderTargetBitmap(
                Math.Max(1, capture.PixelWidth), Math.Max(1, capture.PixelHeight), 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            return target;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or InvalidOperationException)
        {
            // Not worth losing the backdrop over; the unblurred capture still
            // reads as a backdrop under the tint.
            System.Diagnostics.Debug.WriteLine($"[Hearth] backdrop blur failed: {ex.Message}");
            return null;
        }
    }

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int COLORONCOLOR = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(IntPtr dest, int x, int y, int width, int height,
        IntPtr source, int sx, int sy, int sourceWidth, int sourceHeight, int rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}
