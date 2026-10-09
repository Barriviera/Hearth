using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Hearth.App.Tablet;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;

namespace Hearth.App.Bar;

/// <summary>
/// Opens one of Windows' own panels — quick settings, the notification centre —
/// and moves it to the button that asked for it.
///
/// Windows anchors these to its own taskbar's tray, which is the right answer
/// when that is the bar you clicked and the wrong one when it is not. With
/// Hearth's menu bar along the top and the Windows taskbar along the bottom,
/// clicking the control centre button opened the panel in the far corner of
/// the screen, which is what the user reported.
///
/// There is no supported way to tell Windows where to put it, so the panel is
/// moved after the fact. The window is found by watching for one that was not
/// there a moment ago and belongs to the shell — rather than by class name,
/// which changes between Windows builds and is the sort of thing that breaks
/// silently on an update. If nothing turns up, the panel simply stays where
/// Windows put it; nothing is worse than before.
/// </summary>
internal static class ShellFlyouts
{
    /// <summary>Quick settings (Win+A), moved to <paramref name="anchor"/>.</summary>
    public static void QuickSettings(FrameworkElement anchor, ScreenEdge edge) =>
        Open('A', anchor, edge);

    /// <summary>The notification centre and calendar (Win+N).</summary>
    public static void Notifications(FrameworkElement anchor, ScreenEdge edge) =>
        Open('N', anchor, edge);

    private static async void Open(char chord, FrameworkElement anchor, ScreenEdge edge)
    {
        try
        {
            var before = VisibleShellWindows();
            WindowTools.SendWinChord(chord);

            // It takes a moment to appear, and the first frames are animating,
            // so it is moved as soon as it has a real size.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 900)
            {
                await Task.Delay(40).ConfigureAwait(true);

                var panel = VisibleShellWindows().FirstOrDefault(w => !before.Contains(w.Handle) && IsPanel(w));
                if (panel.Handle == IntPtr.Zero) continue;

                MoveTo(panel, anchor, edge);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"bar: opening the Win+{chord} panel", ex);
        }
    }

    /// <summary>Flyout-shaped: big enough to be a panel, small enough not to be a window.</summary>
    private static bool IsPanel(ShellWindow window)
    {
        var width = window.Rect.Right - window.Rect.Left;
        var height = window.Rect.Bottom - window.Rect.Top;
        if (width < 200 || height < 200) return false;
        return width < 1000 && height < 1400;
    }

    private static void MoveTo(ShellWindow panel, FrameworkElement anchor, ScreenEdge edge)
    {
        var source = PresentationSource.FromVisual(anchor);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        if (scale <= 0) scale = 1;

        Point origin;
        try { origin = anchor.PointToScreen(new Point(0, 0)); }
        catch (InvalidOperationException) { return; }

        var width = panel.Rect.Right - panel.Rect.Left;
        var height = panel.Rect.Bottom - panel.Rect.Top;
        var gap = (int)Math.Round(10 * scale);
        var anchorWidth = (int)Math.Round(anchor.ActualWidth * scale);
        var anchorHeight = (int)Math.Round(anchor.ActualHeight * scale);

        var x = (int)Math.Round(origin.X) + anchorWidth / 2 - width / 2;
        var y = edge switch
        {
            ScreenEdge.Top => (int)Math.Round(origin.Y) + anchorHeight + gap,
            _ => (int)Math.Round(origin.Y) - height - gap,
        };
        if (edge is ScreenEdge.Left or ScreenEdge.Right)
        {
            x = edge == ScreenEdge.Left
                ? (int)Math.Round(origin.X) + anchorWidth + gap
                : (int)Math.Round(origin.X) - width - gap;
            y = (int)Math.Round(origin.Y) + anchorHeight / 2 - height / 2;
        }

        // On the display it was opened from, inside its work area.
        var display = Displays.All().FirstOrDefault(d =>
                          origin.X >= d.Bounds.Left && origin.X < d.Bounds.Right &&
                          origin.Y >= d.Bounds.Top && origin.Y < d.Bounds.Bottom)
                      ?? Displays.All().FirstOrDefault();
        if (display is not null)
        {
            x = Math.Clamp(x, display.Bounds.Left + gap, Math.Max(display.Bounds.Left + gap, display.Bounds.Right - width - gap));
            y = Math.Clamp(y, display.Bounds.Top + gap, Math.Max(display.Bounds.Top + gap, display.Bounds.Bottom - height - gap));
        }

        SetWindowPos(panel.Handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Log.Write($"bar: moved the shell panel ({panel.Class}) to the button at ({x},{y})");
    }

    private readonly record struct ShellWindow(IntPtr Handle, string Class, RECT Rect);

    /// <summary>
    /// The shell's own visible top-level windows. Matched by process rather
    /// than by class: the classes these panels use have changed more than once
    /// across Windows 11 builds, the processes have not.
    /// </summary>
    private static List<ShellWindow> VisibleShellWindows()
    {
        var found = new List<ShellWindow>();
        var shellProcesses = new HashSet<int>();

        foreach (var name in new[] { "explorer", "ShellExperienceHost", "ShellHost", "StartMenuExperienceHost" })
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (process) shellProcesses.Add(process.Id);
            }
        }
        if (shellProcesses.Count == 0) return found;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!shellProcesses.Contains(pid)) return true;
            if (!GetWindowRect(hwnd, out var rect)) return true;

            var buffer = new StringBuilder(96);
            GetClassName(hwnd, buffer, buffer.Capacity);
            found.Add(new ShellWindow(hwnd, buffer.ToString(), rect));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool Contains(this List<ShellWindow> windows, IntPtr handle) =>
        windows.Any(w => w.Handle == handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
