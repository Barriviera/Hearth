using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Bar;

/// <summary>
/// Blurs what is behind a bar, by taking a picture of it.
///
/// The first version used <c>SetWindowCompositionAttribute</c> with
/// ACCENT_ENABLE_ACRYLICBLURBEHIND, which is what Explorer's own bars use — and
/// on this machine it blurred nothing at all. The reason is the same one the
/// Start menu ran into and solved a different way: **Windows' blur effects are
/// switched off here** (Energy Saver does that), and every system backdrop
/// silently degrades to a flat wash when they are. Start does not use them; it
/// grabs the pixels behind itself and blurs them itself, and the user's verdict
/// on that was that it "works 110%". So the bars do the same thing.
///
/// A bar is a better case for it than the Start menu, too: it is a thin strip,
/// so the capture is small, and it does not move. It is retaken when the bar is
/// placed, when the wallpaper changes, and on a slow timer, because a snapshot
/// cannot follow a window moving underneath it — the same trade the Start menu
/// makes, and for a strip at the edge of the screen it is barely noticeable.
/// </summary>
internal static class BarGlass
{
    /// <summary>How far past its own edges the capture reaches, so the blur has real pixels to sample.</summary>
    private const int Bleed = 48;

    /// <summary>
    /// Paints <paramref name="host"/> with a blurred picture of the screen
    /// behind <paramref name="window"/>, under a tint. Returns false when
    /// there was nothing to capture.
    /// </summary>
    public static bool Capture(Window window, Border host, double opacity)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            var source = PresentationSource.FromVisual(window);
            var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (scale <= 0) scale = 1;

            var width = (int)Math.Round(window.ActualWidth * scale);
            var height = (int)Math.Round(window.ActualHeight * scale);
            if (width <= 0 || height <= 0) return false;

            var left = (int)Math.Round(window.Left * scale);
            var top = (int)Math.Round(window.Top * scale);
            var margin = (int)Math.Round(Bleed * scale);

            // Taken with the bar off screen, or it photographs itself.
            var area = new Int32Rect(left, top, width, height);
            var capture = WithoutSelf(hwnd, () => StartBackdropCapture.Capture(area, margin));
            if (capture is null) return false;

            var tint = ((SolidColorBrush)BarStyleSheet.Surface).Color;
            tint.A = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
            var tintBrush = new SolidColorBrush(tint);
            tintBrush.Freeze();

            host.Background = Brushes.Transparent;
            host.Child = null;
            var backdrop = StartBackdropCapture.Build(capture, Bleed, tintBrush);
            host.Child = new Grid { ClipToBounds = true, Children = { backdrop } };
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("bar: capturing the backdrop", ex);
            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="take"/> with the window moved out of the way, so
    /// the capture is of what is behind it rather than of the bar itself.
    /// Two SetWindowPos calls and no repaint in between; nothing is on screen
    /// long enough to flicker.
    /// </summary>
    private static T? WithoutSelf<T>(IntPtr hwnd, Func<T?> take)
    {
        if (!GetWindowRect(hwnd, out var rect)) return take();

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        try
        {
            return take();
        }
        finally
        {
            SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
