using System.Runtime.InteropServices;
using System.Windows.Media;
using Hearth.App.Tablet;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Bar;

/// <summary>One top-level window a bar can show a button for.</summary>
internal sealed record BarWindowEntry(IntPtr Handle, string Title, string Program, string ProgramPath)
{
    public bool Minimized { get; init; }
    public bool Foreground { get; init; }
    public int Monitor { get; init; }
    public ImageSource? Icon { get; init; }
}

/// <summary>Every window of one program, in the order they were opened.</summary>
internal sealed record BarAppGroup(string ProgramPath, string Program, IReadOnlyList<BarWindowEntry> Windows)
{
    /// <summary>Kept on the bar whether or not it is running.</summary>
    public bool Pinned { get; init; }

    public ImageSource? Icon => Windows.Select(w => w.Icon).FirstOrDefault(i => i is not null);
    public bool Foreground => Windows.Any(w => w.Foreground);
    public bool Minimized => Windows.Count > 0 && Windows.All(w => w.Minimized);
    public string Title => Windows.Count == 1 ? Windows[0].Title : Program;
}

/// <summary>
/// The live list of windows every bar style draws from, read once and shared,
/// so five presentations do not each run their own EnumWindows.
///
/// The shell has no "tell me about every window" event, so this is a WinEvent
/// hook over the whole desktop plus a slow poll as a backstop. The hook fires
/// far more often than anything changes (a caret moving is a name change on
/// some apps), so every event is coalesced into one rebuild a short time
/// later, and the rebuild only raises <see cref="Changed"/> when the list it
/// produced differs from the last one.
/// </summary>
internal sealed class WindowList : IDisposable
{
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly System.Windows.Threading.DispatcherTimer _coalesce;
    private readonly System.Windows.Threading.DispatcherTimer _poll;
    private readonly List<IntPtr> _hooks = [];
    private readonly Dictionary<IntPtr, ImageSource?> _icons = [];
    private WinEventDelegate? _callback;
    private string _signature = string.Empty;
    private bool _disposed;

    /// <summary>Raised on the UI thread when the window list really changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<BarWindowEntry> Windows { get; private set; } = [];

    public IReadOnlyList<BarAppGroup> Groups { get; private set; } = [];

    public WindowList(System.Windows.Threading.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        _coalesce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _coalesce.Tick += (_, _) => { _coalesce.Stop(); Rebuild(); };

        // Windows that are created, destroyed or renamed without an event we
        // hooked (some cross-process cases) are picked up here.
        _poll = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _poll.Tick += (_, _) => Rebuild();
        _poll.Start();

        _callback = OnWinEvent;
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY);
        Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE);

        Rebuild();
    }

    private void Hook(uint first, uint last)
    {
        // Own-process events are wanted now that Hearth's settings window is
        // listed like any other; the 3 s poll would otherwise be the only thing
        // that ever noticed it opening.
        var hook = SetWinEventHook(first, last, IntPtr.Zero, _callback!, 0, 0, WINEVENT_OUTOFCONTEXT);
        if (hook != IntPtr.Zero) _hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint type, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        // Only the window itself, not its scrollbars, menu items or carets.
        if (obj != OBJID_WINDOW || child != 0 || hwnd == IntPtr.Zero) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _coalesce.Stop();
            _coalesce.Start();
        });
    }

    /// <summary>Rebuilds now, for a caller that has just changed something itself.</summary>
    public void Refresh() => Rebuild();

    private void Rebuild()
    {
        if (_disposed) return;
        try
        {
            var foreground = WindowTools.GetForegroundWindow();
            var live = new List<BarWindowEntry>();

            foreach (var hwnd in WindowTools.SwitchableWindows())
            {
                var path = WindowTools.ProcessPath(hwnd);
                live.Add(new BarWindowEntry(
                    hwnd,
                    WindowTools.Title(hwnd),
                    WindowTools.ProgramName(hwnd),
                    path)
                {
                    Minimized = WindowTools.IsMinimized(hwnd),
                    Foreground = hwnd == foreground,
                    Monitor = (int)MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST),
                    Icon = IconFor(hwnd),
                });
            }

            // Cheap to compare, and it keeps a hook storm from rebuilding five
            // presentations' worth of buttons a hundred times a second.
            var signature = string.Join("|", live.Select(w =>
                $"{w.Handle}:{w.Title}:{(w.Minimized ? 'm' : '-')}{(w.Foreground ? 'f' : '-')}:{w.Monitor}"));
            if (signature == _signature) return;
            _signature = signature;

            Windows = live;
            Groups = live
                .GroupBy(w => w.ProgramPath.Length > 0 ? w.ProgramPath : w.Program, StringComparer.OrdinalIgnoreCase)
                .Select(g => new BarAppGroup(g.Key, g.First().Program, g.ToList()))
                .ToList();

            foreach (var stale in _icons.Keys.Where(h => live.All(w => w.Handle != h)).ToList())
                _icons.Remove(stale);

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("bar: window list", ex);
        }
    }

    /// <summary>
    /// Window icons are cached per window: <c>WindowTools.Icon</c> sends
    /// WM_GETICON, which blocks on the owning program and would be paid on
    /// every rebuild otherwise.
    /// </summary>
    private ImageSource? IconFor(IntPtr hwnd)
    {
        if (_icons.TryGetValue(hwnd, out var cached)) return cached;
        var icon = WindowTools.Icon(hwnd);
        _icons[hwnd] = icon;
        return icon;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _coalesce.Stop();
        _poll.Stop();
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        _callback = null;
        _icons.Clear();
    }

    private const int OBJID_WINDOW = 0;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private delegate void WinEventDelegate(IntPtr hook, uint type, IntPtr hwnd, int obj, int child, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module,
        WinEventDelegate callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
}
