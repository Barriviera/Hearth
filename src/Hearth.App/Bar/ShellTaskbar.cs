using System.IO;
using System.Runtime.InteropServices;
using Hearth.App.Tablet;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Bar;

/// <summary>
/// Takes the Windows taskbar off the screen without taking it out of service.
///
/// Tablet mode hides the bars with SW_HIDE, which works but costs the tray: a
/// hidden taskbar exposes nothing to UI Automation, so reading the tray icons
/// (and the hidden-icon flyout in particular) meant showing the bar, giving it
/// the foreground, reading, and hiding it again — a visible flash, and 7.4 s
/// measured end to end.
///
/// This does it with an empty window region instead. The bar keeps every one
/// of its properties — visible, positioned, focusable, fully populated — and
/// simply has no area: nothing of it is painted, and WindowFromPoint over its
/// strip returns whatever is behind it, so clicks land on Hearth. Measured on
/// this machine: the same tray read that took 7379 ms through show-and-hide
/// took 883 ms with the region on, and nothing flashed.
///
/// Auto-hide is turned on alongside it so Windows stops reserving the strip in
/// the work area and Hearth's own bar can dock there. The bar never actually
/// slides, because nothing can reach an edge it no longer occupies.
///
/// Every change is recorded in the same shell-state file tablet mode uses, so
/// the watchdog, <c>--restore-shell</c> and the next start all put it back.
/// </summary>
internal static class ShellTaskbar
{
    private static bool _regionApplied;

    public static bool IsHiddenByUs => _regionApplied;

    /// <summary>Hides the bars and frees their strip. Safe to call repeatedly.</summary>
    public static void Hide()
    {
        var state = ShellState.Load();
        if (!state.TaskbarHidden)
        {
            state.TaskbarWasAutoHide = TaskbarControl.AutoHide;
            state.TaskbarHidden = true;
        }
        state.OwnerProcessId = Environment.ProcessId;
        state.Save();

        // The order here is the whole trick, and it took a while to find.
        //
        // The region makes the taskbar invisible and click-through. It does
        // **not** release the strip: Explorer keeps reserving the work area for
        // a taskbar with no area. Only auto-hide releases it.
        //
        // And Explorer will not turn auto-hide on for a taskbar whose region is
        // already empty — ABM_SETSTATE returns cleanly and ABM_GETSTATE still
        // reads 0. A region survives on Explorer's own window after Hearth
        // exits, so a run that ended badly leaves the next one unable to set
        // auto-hide at all, which is how the user ended up with an invisible
        // taskbar still holding 32 px.
        //
        // So: give the region back first, set auto-hide, then take the area
        // away again.
        foreach (var bar in TaskbarControl.Bars()) SetWindowRgn(bar, IntPtr.Zero, false);
        if (!TaskbarControl.AutoHide) TaskbarControl.AutoHide = true;
        WriteAutoHidePreference(true);

        _regionApplied = true;
        Apply();

        Log.Write($"bar: Windows taskbar hidden by region (auto-hide was {(state.TaskbarWasAutoHide ? "on" : "off")}, " +
                  $"now {(TaskbarControl.AutoHide ? "on" : "not yet — needs Explorer to reload")})");
    }

    /// <summary>
    /// Re-applies the region. Explorer drops it whenever it rebuilds the bar
    /// (a display change, a settings change, its own restart), so the bar
    /// service calls this on a timer and after every such event.
    /// </summary>
    public static void Apply()
    {
        if (!_regionApplied) return;
        ApplyRegions();
    }

    private static void ApplyRegions()
    {
        foreach (var bar in TaskbarControl.Bars())
        {
            // An empty region is the whole point; a null one would mean "the
            // whole window" and put the taskbar straight back.
            var region = CreateRectRgn(0, 0, 0, 0);
            if (region == IntPtr.Zero) continue;
            // The window owns the region afterwards, so it is never deleted here.
            if (SetWindowRgn(bar, region, true) == 0) DeleteObject(region);
        }
    }

    /// <summary>
    /// Whether the taskbar is still holding its strip: hidden by us, but with
    /// auto-hide not actually in force, so the work area stops short of an edge
    /// nothing occupies.
    /// </summary>
    public static bool StripStillReserved => _regionApplied && !TaskbarControl.AutoHide;

    /// <summary>
    /// Writes Windows' own taskbar auto-hide setting.
    ///
    /// On Windows 11 this is the only thing that works. <c>ABM_SETSTATE</c> is
    /// simply ignored — verified on build 26200 from outside Hearth as well, so
    /// it is the OS and not this code — and <c>SPI_SETWORKAREA</c> is overridden
    /// again by Explorer within seconds. The setting lives in the low bit of
    /// byte 8 of StuckRects3, and Explorer reads it when it starts. So Hearth
    /// writes it and says plainly that it takes effect when Explorer reloads,
    /// rather than pretending the strip has been freed.
    /// </summary>
    public static void WriteAutoHidePreference(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StuckRects, writable: true);
            if (key?.GetValue("Settings") is not byte[] settings || settings.Length <= 8) return;

            var already = (settings[8] & 1) != 0;
            if (already == on) return;

            settings[8] = (byte)(on ? settings[8] | 1 : settings[8] & ~1);
            key.SetValue("Settings", settings, Microsoft.Win32.RegistryValueKind.Binary);
            Log.Write($"bar: Windows taskbar auto-hide written as {(on ? "on" : "off")}; Explorer applies it when it next loads");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"bar: could not write the taskbar auto-hide setting: {ex.Message}");
        }
    }

    /// <summary>
    /// Restarts Explorer, which is what makes the setting above take hold.
    ///
    /// Disruptive enough that Hearth never does it on its own — open File
    /// Explorer windows close — so it is offered as a button and nothing more.
    /// Hearth itself survives: it is not Explorer's child, and it already
    /// handles TaskbarCreated by re-hiding the new bars.
    /// </summary>
    public static bool RestartExplorer()
    {
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("explorer"))
            {
                using (process) process.Kill();
            }
            Log.Write("bar: Explorer restarted so the taskbar setting takes effect");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            Log.Error("bar: restarting Explorer", ex);
            return false;
        }
    }

    private const string StuckRects = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";

    /// <summary>Gives the bars their area and their auto-hide setting back.</summary>
    public static void Restore()
    {
        foreach (var bar in TaskbarControl.Bars()) SetWindowRgn(bar, IntPtr.Zero, true);
        _regionApplied = false;

        var state = ShellState.Load();
        if (!state.TaskbarHidden) return;
        WriteAutoHidePreference(state.TaskbarWasAutoHide);
        if (!state.TaskbarWasAutoHide && TaskbarControl.AutoHide) TaskbarControl.AutoHide = false;
        state.TaskbarHidden = false;
        state.Save();
        Log.Write($"bar: Windows taskbar restored (auto-hide {(state.TaskbarWasAutoHide ? "left on" : "turned off again")})");
    }

    /// <summary>
    /// Hides tooltips and flyouts Explorer has left anchored to a taskbar that
    /// is no longer on screen.
    ///
    /// Reading the tray makes Explorer put up the chevron's own tooltip
    /// ("Show hidden icons"), which normally sits against the taskbar and
    /// disappears when the pointer leaves it. With the taskbar given no area,
    /// the pointer never was on it, so nothing ever takes the tooltip away and
    /// it is left floating over the wallpaper. Only popups overlapping the
    /// taskbar's own rect are touched, so a flyout the user opened somewhere
    /// else is left alone.
    /// </summary>
    /// <summary>
    /// Takes back the strip the hidden taskbar is still reserving, without
    /// waiting for Explorer to reload.
    ///
    /// The setting Windows actually reads is only read at start-up, so writing
    /// it fixes tomorrow and not today — and the user, reasonably, wanted the
    /// space now. The work area itself can be set directly, and it holds until
    /// Explorer recalculates for a reason of its own, so it is re-asserted on
    /// the same timer that keeps the region applied.
    ///
    /// <paramref name="ours"/> is what Hearth's own docked bars are claiming;
    /// those strips stay reserved, because they really are occupied. Everything
    /// else on the display goes back to the applications.
    /// </summary>
    public static void ReclaimStrips(IEnumerable<(int Left, int Top, int Right, int Bottom)> ours)
    {
        if (!_regionApplied) return;
        try
        {
            var claims = ours.Select(s => new RECT { Left = s.Left, Top = s.Top, Right = s.Right, Bottom = s.Bottom }).ToList();
            foreach (var bounds in MonitorBounds())
            {
                var wanted = bounds;
                foreach (var strip in claims)
                {
                    // Only the strips on this display.
                    var cx = (strip.Left + strip.Right) / 2;
                    var cy = (strip.Top + strip.Bottom) / 2;
                    if (cx < bounds.Left || cx >= bounds.Right || cy < bounds.Top || cy >= bounds.Bottom) continue;
                    Subtract(ref wanted, strip, bounds);
                }

                if (!GetWorkArea(bounds, out var actual)) continue;
                if (Same(actual, wanted)) continue;

                // Only ever hand space back. If something else has legitimately
                // claimed more than Hearth knows about, leave it alone rather
                // than fight over it.
                if (actual.Left <= wanted.Left && actual.Top <= wanted.Top &&
                    actual.Right >= wanted.Right && actual.Bottom >= wanted.Bottom) continue;

                Apply(wanted);
                Log.Write($"bar: took back the strip the hidden taskbar was holding; " +
                          $"work area is now ({wanted.Left},{wanted.Top})-({wanted.Right},{wanted.Bottom})");
            }
        }
        catch (Exception ex)
        {
            Log.Error("bar: reclaiming the taskbar's strip", ex);
        }
    }

    /// <summary>Removes a docked strip from a work area, on whichever edge it sits.</summary>
    private static void Subtract(ref RECT area, RECT strip, RECT screen)
    {
        var width = strip.Right - strip.Left;
        var height = strip.Bottom - strip.Top;
        if (width <= 0 || height <= 0) return;

        // The edge it is on is the one it is flush against.
        if (strip.Top <= screen.Top + 1 && height < screen.Bottom - screen.Top) area.Top = Math.Max(area.Top, strip.Bottom);
        else if (strip.Bottom >= screen.Bottom - 1 && height < screen.Bottom - screen.Top) area.Bottom = Math.Min(area.Bottom, strip.Top);
        else if (strip.Left <= screen.Left + 1) area.Left = Math.Max(area.Left, strip.Right);
        else if (strip.Right >= screen.Right - 1) area.Right = Math.Min(area.Right, strip.Left);
    }

    private static bool GetWorkArea(RECT bounds, out RECT work)
    {
        work = default;
        var centre = new POINT((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
        var monitor = MonitorFromPoint(centre, 2);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        work = info.rcWork;
        return true;
    }

    private static void Apply(RECT area)
    {
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.StructureToPtr(area, memory, false);
            SystemParametersInfo(SPI_SETWORKAREA, 0, memory, SPIF_SENDCHANGE);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static bool Same(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    public static void DismissOrphanedPopups()
    {
        if (!_regionApplied) return;
        try
        {
            var bars = TaskbarControl.Bars().Where(b => GetWindowRect(b, out _)).ToList();
            if (bars.Count == 0) return;

            var areas = new List<RECT>();
            foreach (var bar in bars)
            {
                if (!GetWindowRect(bar, out var rect)) continue;
                areas.Add(new RECT
                {
                    Left = rect.Left - 80,
                    Top = rect.Top - 80,
                    Right = rect.Right + 80,
                    Bottom = rect.Bottom + 80,
                });
            }

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (ClassOf(hwnd) != "Xaml_WindowedPopupClass") return true;
                if (!GetWindowRect(hwnd, out var popup)) return true;
                if (!areas.Any(a => popup.Left < a.Right && popup.Right > a.Left &&
                                    popup.Top < a.Bottom && popup.Bottom > a.Top)) return true;
                ShowWindow(hwnd, SW_HIDE);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("bar: clearing leftover taskbar popups", ex);
        }
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(64);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const int SW_HIDE = 0;

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    /// <summary>
    /// Gives every display its whole work area back, letting the app bars that
    /// are still alive claim their strips again.
    ///
    /// An app bar that dies without releasing its strip leaves the reservation
    /// behind, and Windows keeps honouring it: every window on that display
    /// stops short of an edge nothing occupies any more, which is what the
    /// user saw as "the system still thinks the taskbar is up there". Passing a
    /// null rectangle to SPI_SETWORKAREA resets it, which is what an Explorer
    /// restart does and the only way back without one.
    ///
    /// Safe to call at start-up: live app bars re-claim within a moment.
    /// </summary>
    public static void ResetWorkArea()
    {
        try
        {
            // A null rectangle is documented as "reset", and on this build it
            // simply returns false. An explicit rectangle works: each display
            // is given its whole self back, and the app bars that are still
            // alive claim their strips again within the moment.
            var reset = 0;
            foreach (var bounds in MonitorBounds())
            {
                var rect = bounds;
                var memory = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
                try
                {
                    Marshal.StructureToPtr(rect, memory, false);
                    if (SystemParametersInfo(SPI_SETWORKAREA, 0, memory, SPIF_SENDCHANGE)) reset++;
                }
                finally
                {
                    Marshal.FreeHGlobal(memory);
                }
            }
            if (reset > 0) Log.Write($"bar: work area reset on {reset} display(s); any strip left behind is released");
        }
        catch (Exception ex)
        {
            Log.Error("bar: resetting the work area", ex);
        }
    }

    private static List<RECT> MonitorBounds()
    {
        var found = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info)) found.Add(info.rcMonitor);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private const uint SPI_SETWORKAREA = 0x002F;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint notify);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
}
