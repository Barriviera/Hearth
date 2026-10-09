using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Hearth.App.Hosting;
using Hearth.Core.Settings;

namespace Hearth.App.Bar;

/// <summary>
/// The two things every panel a bar opens has to get right, and that both the
/// control centre and the stacks got wrong.
///
/// **Where it goes.** A window that has not been shown has no size, so
/// positioning it from <c>DesiredSize</c> before <c>Show</c> puts it wherever
/// zero height lands — against the bottom edge, with a sliver visible. It is
/// shown off screen first, given a layout pass, and only then moved into
/// place, which is the same order the Start menu uses.
///
/// **How it closes.** These are opened from bars that never take focus, so the
/// foreground never moves to them and <c>Deactivated</c> never fires — which is
/// why clicking away left them on screen. Taking the foreground deliberately,
/// once shown, is what makes a click anywhere else close them.
/// </summary>
internal static class BarFlyout
{
    private const int Offscreen = -32000;

    /// <summary>Shows <paramref name="window"/> laid out, in place, and focused.</summary>
    public static void Present(Window window, FrameworkElement anchor, ScreenEdge edge, double gap = 8)
    {
        window.Left = Offscreen;
        window.Top = Offscreen;
        window.Show();

        // Now it has a size worth asking about.
        window.UpdateLayout();
        var size = new Size(window.ActualWidth, window.ActualHeight);
        if (size.Width <= 0 || size.Height <= 0) size = window.DesiredSize;

        var (left, top) = Place(anchor, edge, size, gap);
        window.Left = left;
        window.Top = top;

        // Opened from a bar that never activates, so the foreground has to be
        // claimed by hand or nothing will ever deactivate this.
        var hwnd = new WindowInteropHelper(window).Handle;
        StartTrigger.ClaimForegroundRight();
        window.Activate();
        if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
    }

    private static (double Left, double Top) Place(FrameworkElement anchor, ScreenEdge edge, Size size, double gap)
    {
        var source = PresentationSource.FromVisual(anchor);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        if (scale <= 0) scale = 1;

        var origin = anchor.PointToScreen(new Point(0, 0));
        double left = origin.X / scale, top = origin.Y / scale;
        var width = anchor.ActualWidth;
        var height = anchor.ActualHeight;

        switch (edge)
        {
            case ScreenEdge.Top:
                top += height + gap;
                left += width / 2 - size.Width / 2;
                break;
            case ScreenEdge.Left:
                left += width + gap;
                top += height / 2 - size.Height / 2;
                break;
            case ScreenEdge.Right:
                left -= size.Width + gap;
                top += height / 2 - size.Height / 2;
                break;
            default:
                top -= size.Height + gap;
                left += width / 2 - size.Width / 2;
                break;
        }

        // Kept on the display it was opened from, and inside its work area, so
        // a panel opened from a corner never hangs off the screen.
        var display = Displays.All().FirstOrDefault(d =>
                          origin.X >= d.Bounds.Left && origin.X < d.Bounds.Right &&
                          origin.Y >= d.Bounds.Top && origin.Y < d.Bounds.Bottom)
                      ?? Displays.All().FirstOrDefault();
        if (display is not null)
        {
            var minLeft = display.Bounds.Left / scale + 6;
            var maxLeft = display.Bounds.Right / scale - size.Width - 6;
            var minTop = display.Bounds.Top / scale + 6;
            var maxTop = display.Bounds.Bottom / scale - size.Height - 6;
            if (maxLeft > minLeft) left = Math.Clamp(left, minLeft, maxLeft);
            if (maxTop > minTop) top = Math.Clamp(top, minTop, maxTop);
        }
        return (left, top);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
