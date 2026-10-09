using System.Runtime.InteropServices;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Hosting;

/// <summary>
/// Closes another program's menu or flyout when the user clicks Hearth.
///
/// A menu closes because its owner stops being the foreground window. Hearth's
/// desktop is WS_EX_NOACTIVATE and never takes the foreground, so clicking it
/// leaves whatever was open exactly where it was — the user reported having to
/// click an empty strip of taskbar to get rid of a tray menu or a shell
/// flyout. So the click is passed on by hand:
///
///   - a classic menu (anything built on TrackPopupMenu: tray applications,
///     Explorer's own) is cancelled through its owner, because EndMenu only
///     works on the calling thread;
///   - a XAML flyout (Start, quick settings, the calendar, the hidden-icon
///     tray) is sent Escape, which is what the tray code already uses to close
///     the overflow flyout.
///
/// Escape only ever goes to a foreground window that is not one of ours and is
/// not switchable — that is, not a window Alt+Tab would list — so an ordinary
/// application never sees it.
/// </summary>
internal static class ForeignPopups
{
    /// <summary>
    /// Called for every click on one of Hearth's non-activating surfaces.
    /// Returns true when something was actually dismissed.
    /// </summary>
    public static bool DismissForClick()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            var thread = GetWindowThreadProcessId(foreground, out var pid);
            if (pid == Environment.ProcessId) return false;

            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(thread, ref info) && (info.flags & (GUI_INMENUMODE | GUI_POPUPMENU)) != 0)
            {
                var owner = info.hwndMenuOwner != IntPtr.Zero ? info.hwndMenuOwner : foreground;
                PostMessage(owner, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                Log.Write($"click: cancelled a menu owned by 0x{owner:X}");
                return true;
            }

            if (Tablet.WindowTools.IsSwitchable(foreground)) return false;

            PostMessage(foreground, WM_KEYDOWN, new IntPtr(VK_ESCAPE), new IntPtr(0x00010001));
            PostMessage(foreground, WM_KEYUP, new IntPtr(VK_ESCAPE), new IntPtr(unchecked((int)0xC0010001)));

            // Escape is not enough for the shell's XAML flyouts — quick
            // settings, the calendar, the notification centre. They close on
            // losing activation and nothing else, and a click on one of
            // Hearth's non-activating surfaces never takes it from them. So the
            // activation is taken by a window of ours that has no pixels: it
            // becomes the foreground for an instant, the flyout deactivates and
            // closes, and the foreground is handed straight back to whatever
            // the user was actually working in.
            StealActivation(foreground);

            Log.Write($"click: dismissed the flyout 0x{foreground:X} ({Tablet.WindowTools.ClassName(foreground)})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("dismissing another program's menu", ex);
            return false;
        }
    }

    private static IntPtr _thief;

    /// <summary>
    /// Takes the foreground for an instant with an invisible window of ours, so
    /// a flyout that only closes on deactivation closes, then gives it to the
    /// window the user was last really using.
    ///
    /// The window is one pixel, off screen, WS_EX_TOOLWINDOW and never shown in
    /// any list. It is kept rather than made each time: creating and destroying
    /// a window on every click is more disruptive than owning one.
    /// </summary>
    private static void StealActivation(IntPtr flyout)
    {
        try
        {
            if (_thief == IntPtr.Zero) _thief = CreateThief();
            if (_thief == IntPtr.Zero) return;

            // Where to hand it back: the top switchable window that is not the
            // flyout and not one of ours.
            var giveBackTo = Tablet.WindowTools.SwitchableWindows()
                .FirstOrDefault(w => w != flyout && !Tablet.WindowTools.IsOwnProcess(w));

            StartTrigger.ClaimForegroundRight();
            ShowWindow(_thief, SW_SHOWNA);
            SetForegroundWindow(_thief);
            ShowWindow(_thief, SW_HIDE);

            if (giveBackTo != IntPtr.Zero)
            {
                StartTrigger.ClaimForegroundRight();
                SetForegroundWindow(giveBackTo);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"click: could not take the activation back: {ex.Message}");
        }
    }

    private static IntPtr CreateThief() =>
        CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE_STYLE, "STATIC", "Hearth dismiss",
            0, -32000, -32000, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    private const int WS_EX_TOOLWINDOW = 0x00000080;

    // Deliberately NOT WS_EX_NOACTIVATE: this window's whole job is to be
    // activated. The constant is named so nobody adds it back by reflex.
    private const int WS_EX_NOACTIVATE_STYLE = 0;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    private const int GUI_INMENUMODE = 0x00000004;
    private const int GUI_POPUPMENU = 0x00000010;
    private const int WM_CANCELMODE = 0x001F;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int VK_ESCAPE = 0x1B;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
