using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Hearth.App.Hosting;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;

namespace Hearth.App.Bar;

/// <summary>How a bar sits on its edge.</summary>
internal enum BarDock
{
    /// <summary>The whole edge, registered as an app bar so maximized windows stop at it.</summary>
    Strip,

    /// <summary>A capsule centred on its edge, sized to its contents, floating over the windows.</summary>
    Floating,
}

/// <summary>
/// The window every bar-shaped style lives in: a strip or a capsule on one
/// edge of one display, which never takes focus.
///
/// Not taking focus is not a detail. A bar that activated would become the
/// window in front, so the button you just clicked would be showing you a
/// window that is no longer the foreground one, and closing a window from the
/// bar would leave the bar itself in front instead of the next window. The
/// same rule the navigation bar follows: WS_EX_NOACTIVATE, and MA_NOACTIVATE
/// for every click.
///
/// A <see cref="BarDock.Strip"/> registers with the shell (ABM_NEW / ABM_SETPOS)
/// so it reserves its strip. Those calls block for one to two seconds while
/// Explorer reflows every window, so the window is placed where it will end up
/// first and the claim runs off the UI thread; app bars are also told about
/// each other's moves, so a re-dock that would not move anything is dropped.
/// </summary>
internal sealed class BarWindow : Window
{
    private readonly BarService _service;
    private readonly BarDock _mode;
    private readonly ScreenEdge _edge;
    private readonly double _thickness;
    private readonly DisplayTarget _display;
    private readonly uint _callbackMessage;
    private readonly bool _reserveSpace;
    private IntPtr _hwnd;
    private bool _registered;
    private bool _allowClose;
    private bool _docking;
    private bool _dockAgain;
    private DispatcherTimer? _redock;
    private RECT _docked;

    public ScreenEdge Edge => _edge;

    /// <summary>This bar's thickness in DIPs, unscaled — the home screen wants
    /// this even when the bar does not reserve any space for it.</summary>
    public double Thickness => _thickness;

    /// <summary>Whether this bar claims a strip of the work area.</summary>
    public bool ReservesSpace => _reserveSpace;

    /// <summary>The strip it has claimed, for working out what the work area should be.</summary>
    public (int Left, int Top, int Right, int Bottom) DockedStrip =>
        (_docked.Left, _docked.Top, _docked.Right, _docked.Bottom);

    /// <summary>A floating bar sits over the windows rather than on an edge of its own.</summary>
    public bool Floating => _mode == BarDock.Floating;

    /// <summary>This bar's Start button, so the menu can be lined up with it.</summary>
    public FrameworkElement? StartButton { get; set; }

    public DisplayTarget Display => _display;

    /// <summary>The element styles fill with their own content.</summary>
    public Border Surface { get; }

    private readonly Border _glass;

    public BarWindow(BarService service, DisplayTarget display, ScreenEdge edge, double thickness,
        BarDock mode, bool reserveSpace)
    {
        _service = service;
        _display = display;
        _edge = edge;
        _mode = mode;
        _thickness = thickness;
        _reserveSpace = reserveSpace;
        _callbackMessage = RegisterWindowMessage("Hearth.Bar.AppBar");

        Title = "Hearth bar";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = StartStyle.Body;
        Foreground = StartStyle.Text;
        Resources = StartStyle.WindowResources();
        Background = Brushes.Transparent;
        // A floating capsule needs its corners cut out of the window.
        AllowsTransparency = mode == BarDock.Floating;
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;

        var corners = mode == BarDock.Floating
            ? service.Settings.DockCorners(thickness)
            : new CornerRadius(0);

        // The blurred picture of what is behind, under everything else. It is
        // its own layer because the content has to sit on top of it and the
        // capture is replaced whenever the bar moves.
        _glass = new Border
        {
            CornerRadius = corners,
            IsHitTestVisible = false,
        };
        // ClipToBounds on a Border clips to its rectangle, not to its corner
        // radius, so a blurred backdrop squared off the dock's rounded ends.
        // A rounded clip geometry is the only thing that actually follows the
        // shape, and it has to be resized with the window.
        _glass.SizeChanged += (_, e) =>
        {
            var radius = Math.Max(0, corners.TopLeft);
            _glass.Clip = new RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
        };

        Surface = new Border
        {
            // With the blur on, the wash is painted into the captured backdrop
            // below; a second opaque surface on top would hide it entirely.
            Background = service.Settings.Blur ? Brushes.Transparent : BarStyleSheet.Surface,
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = mode == BarDock.Floating ? new Thickness(1) : EdgeBorder(edge),
            CornerRadius = corners,
        };
        // Anywhere on the bar that is not a button of its own. Every style gets
        // this, which is what the user meant by a right-click menu on all of
        // them — before, only the empty strip between the groups answered.
        Surface.MouseRightButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && HasOwnMenu(source)) return;
            e.Handled = true;
            _service.ShowBarMenu(Surface);
        };
        Content = new Grid { Children = { _glass, Surface } };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            ApplyGlass();
            if (_reserveSpace) Register();
            Dock();
        };
    }

    /// <summary>
    /// The stack button the pointer is over, if any. Clicking a stack's own
    /// button must not close the stack it is about to toggle.
    /// </summary>
    private StackButton? StackUnderPointer()
    {
        try
        {
            if (!IsLoaded) return null;
            var point = Mouse.GetPosition(this);
            var hit = InputHitTest(point) as DependencyObject;
            for (var node = hit; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            {
                if (node is StackButton button) return button;
            }
        }
        catch (InvalidOperationException)
        {
            // Not laid out yet; nothing is under anything.
        }
        return null;
    }

    /// <summary>
    /// Whether the thing under the pointer already answers a right-click, so
    /// the bar's own menu does not replace an app button's.
    /// </summary>
    private static bool HasOwnMenu(DependencyObject source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, null)) break;
            if (node is BarButton or StackButton or BarTray or Views.Start.StartTile) return true;
            if (node is Border border && ReferenceEquals(border, null)) break;
        }
        return false;
    }

    /// <summary>
    /// Retakes the blurred picture behind the bar. Called after every placement
    /// and whenever the background changes, because a snapshot cannot follow
    /// what moves under it.
    /// </summary>
    public void ApplyGlass()
    {
        if (!_service.Settings.Blur)
        {
            _glass.Child = null;
            _glass.Background = null;
            Surface.Background = BarStyleSheet.Surface;
            return;
        }

        Surface.Background = Brushes.Transparent;
        if (!BarGlass.Capture(this, _glass, _service.Settings.Opacity))
        {
            // Nothing to capture yet (the window is not placed): fall back to
            // the flat surface rather than a hole.
            Surface.Background = BarStyleSheet.Surface;
        }
    }

    private static Thickness EdgeBorder(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Bottom => new Thickness(0, 1, 0, 0),
        ScreenEdge.Top => new Thickness(0, 0, 0, 1),
        ScreenEdge.Left => new Thickness(0, 0, 1, 0),
        _ => new Thickness(1, 0, 0, 0),
    };

    public bool Vertical => _edge is ScreenEdge.Left or ScreenEdge.Right;

    // ---- Placement -------------------------------------------------------

    private void Register()
    {
        var data = NewData();
        data.uCallbackMessage = _callbackMessage;
        _registered = SHAppBarMessage(ABM_NEW, ref data) != IntPtr.Zero;
        if (!_registered) Log.Write("bar: could not register as an app bar; windows will not make room for it");
    }

    /// <summary>Places the bar, and claims its strip when it is one.</summary>
    public async void Dock()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_docking)
        {
            _dockAgain = true;
            return;
        }
        _docking = true;
        _dockAgain = false;
        try
        {
            var scale = _display.Scale;
            var thickness = (int)Math.Round(_thickness * scale);
            var screen = _display.Bounds;

            if (_mode == BarDock.Floating)
            {
                Place(FloatingRect(screen, thickness, scale));
                return;
            }

            var data = NewData();
            data.uEdge = _edge switch
            {
                ScreenEdge.Left => ABE_LEFT,
                ScreenEdge.Top => ABE_TOP,
                ScreenEdge.Right => ABE_RIGHT,
                _ => ABE_BOTTOM,
            };
            data.rc = Strip(screen, thickness);
            if (Same(data.rc, _docked)) return;

            var wanted = data.rc;
            Place(wanted);

            if (_registered)
            {
                var request = data;
                data = await Task.Run(() =>
                {
                    var sent = request;
                    SHAppBarMessage(ABM_SETPOS, ref sent);
                    return sent;
                }).ConfigureAwait(true);
                if (!IsVisible) return;
            }

            // Windows' suggestion can still leave room for a taskbar Hearth has
            // taken off the screen, so the wanted rect wins when it has.
            var final = ShellTaskbar.IsHiddenByUs ? wanted : data.rc;
            _docked = final;
            Place(final);
            NudgeMaximizedWindows();
            // The backdrop is of wherever it has just landed.
            Dispatcher.BeginInvoke(ApplyGlass, DispatcherPriority.Loaded);
            Log.Write($"bar: {_edge} strip at ({final.Left},{final.Top})-({final.Right},{final.Bottom}) " +
                      $"on {_display.Name} at {scale * 100:F0}%");
        }
        catch (Exception ex)
        {
            Log.Error("bar dock", ex);
        }
        finally
        {
            _docking = false;
            if (_dockAgain && IsVisible)
            {
                _dockAgain = false;
                _docked = default;
                Dock();
            }
        }
    }

    /// <summary>
    /// Makes maximized windows take the new work area.
    ///
    /// Windows broadcasts the change and most programs re-layout, but some keep
    /// the size they were maximized to and end up short of the bar, with a
    /// strip of wallpaper between them and it — which is what the user saw as
    /// applications "not fully reaching the top". Chromium-based windows and
    /// anything else drawing its own frame are the usual offenders. Asking for
    /// a frame change without moving or resizing makes them recalculate, and
    /// costs nothing for the ones that were already right.
    /// </summary>
    private void NudgeMaximizedWindows()
    {
        try
        {
            foreach (var hwnd in Tablet.WindowTools.SwitchableWindows())
            {
                if (!Tablet.WindowTools.IsMaximized(hwnd)) continue;
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"bar: could not nudge maximized windows: {ex.Message}");
        }
    }

    private RECT Strip(RECT screen, int thickness)
    {
        var rc = screen;
        switch (_edge)
        {
            case ScreenEdge.Left: rc.Right = rc.Left + thickness; break;
            case ScreenEdge.Right: rc.Left = rc.Right - thickness; break;
            case ScreenEdge.Top: rc.Bottom = rc.Top + thickness; break;
            default: rc.Top = rc.Bottom - thickness; break;
        }
        return rc;
    }

    /// <summary>A capsule sized to its contents, centred on its edge with a gap.</summary>
    private RECT FloatingRect(RECT screen, int thickness, double scale)
    {
        Surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wanted = Surface.DesiredSize;
        var gap = (int)Math.Round(10 * scale);

        var length = (int)Math.Round((Vertical ? wanted.Height : wanted.Width) * scale);
        length = Math.Max(thickness, Math.Min(length, (Vertical ? screen.Bottom - screen.Top : screen.Right - screen.Left) - gap * 2));

        var rc = screen;
        if (Vertical)
        {
            var centre = (screen.Top + screen.Bottom) / 2;
            rc.Top = centre - length / 2;
            rc.Bottom = rc.Top + length;
            if (_edge == ScreenEdge.Left) { rc.Left = screen.Left + gap; rc.Right = rc.Left + thickness; }
            else { rc.Right = screen.Right - gap; rc.Left = rc.Right - thickness; }
        }
        else
        {
            var centre = (screen.Left + screen.Right) / 2;
            rc.Left = centre - length / 2;
            rc.Right = rc.Left + length;
            if (_edge == ScreenEdge.Top) { rc.Top = screen.Top + gap; rc.Bottom = rc.Top + thickness; }
            else { rc.Bottom = screen.Bottom - gap; rc.Top = rc.Bottom - thickness; }
        }
        return rc;
    }

    /// <summary>A floating bar is sized to its contents, so it is re-placed whenever they change.</summary>
    public void Resize()
    {
        if (_mode != BarDock.Floating || _hwnd == IntPtr.Zero) return;
        Dispatcher.BeginInvoke(() =>
        {
            Surface.UpdateLayout();
            Place(FloatingRect(_display.Bounds, (int)Math.Round(_thickness * _display.Scale), _display.Scale));
        }, DispatcherPriority.Loaded);
    }

    private void Place(RECT r)
    {
        _shown = r;
        // Placed while away: stay away, at the new spot.
        PlaceAt(_slidePosition);
    }

    private static bool Same(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private void ScheduleDock()
    {
        if (_redock is null)
        {
            _redock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _redock.Tick += (_, _) => { _redock.Stop(); Dock(); };
        }
        _redock.Stop();
        _redock.Start();
    }

    // ---- Auto-hide --------------------------------------------------------

    private DispatcherTimer? _peek;
    private bool _hidden;
    private bool? _clickThrough;
    private RECT _shown;

    /// <summary>
    /// Slides the bar off its edge, leaving a sliver to reach for.
    ///
    /// The strip stays claimed either way: a bar that gave its space back on
    /// hiding would make every window resize twice a second, which is what the
    /// Windows taskbar does and why its auto-hide is unpleasant. Hearth keeps
    /// the reservation and only moves the window, so nothing else on screen
    /// notices.
    /// </summary>
    public void EnableAutoHide(bool enabled)
    {
        if (!enabled)
        {
            _peek?.Stop();
            _peek = null;
            Reveal();
            return;
        }
        if (_peek is not null) return;

        _peek = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(90) };
        _peek.Tick += (_, _) => Peek();
        _peek.Start();
        Conceal();
    }

    /// <summary>How much of the bar is left reachable while it is away.</summary>
    private const int Sliver = 2;

    private DispatcherTimer? _slide;
    private double _slidePosition;   // 0 = fully out, 1 = fully away
    private double _slideTarget;

    private void Peek()
    {
        if (_hwnd == IntPtr.Zero || !GetCursorPos(out var cursor)) return;

        var rect = _shown;
        if (rect.Right <= rect.Left) return;
        var screen = _display.Bounds;

        bool over;
        if (_mode == BarDock.Floating)
        {
            // A floating dock sits a gap clear of the edge — dead space
            // nothing else lives in — so the reach has to cross all of it;
            // nobody aims for a band a few pixels short of a dock that is not
            // actually there.
            var reach = (int)Math.Round(4 * _display.Scale);
            switch (_edge)
            {
                case ScreenEdge.Top: rect.Top = screen.Top; break;
                case ScreenEdge.Bottom: rect.Bottom = screen.Bottom; break;
                case ScreenEdge.Left: rect.Left = screen.Left; break;
                default: rect.Right = screen.Right; break;
            }
            over = cursor.X >= rect.Left - reach && cursor.X <= rect.Right + reach &&
                   cursor.Y >= rect.Top - reach && cursor.Y <= rect.Bottom + reach;
        }
        else
        {
            // A strip flush with the edge gets none of that reach. Using its
            // own full thickness as the hot zone — the whole width of the
            // screen, as tall as the bar itself — was generous enough to eat
            // a maximized window's own close button, which sits in exactly
            // that corner: closing a window meant fighting the bar for the
            // cursor first. Windows' own taskbar only answers to the last
            // couple of pixels at the true edge; a strip asks for the same,
            // and still along its whole length, so the edge itself stays easy
            // to find.
            var reach = Math.Max(1, (int)Math.Round(2 * _display.Scale));
            switch (_edge)
            {
                case ScreenEdge.Top: rect.Bottom = screen.Top + reach; break;
                case ScreenEdge.Bottom: rect.Top = screen.Bottom - reach; break;
                case ScreenEdge.Left: rect.Right = screen.Left + reach; break;
                default: rect.Left = screen.Right - reach; break;
            }
            over = cursor.X >= rect.Left && cursor.X <= rect.Right &&
                   cursor.Y >= rect.Top && cursor.Y <= rect.Bottom;
        }

        // Something filling the whole display changes two things, not one.
        var coversFullScreen = CoversFullScreen(screen);
        SyncFullScreenYield(coversFullScreen);

        // Hovering the edge does not count while covered. That reach runs all
        // the way to the screen's edge, which is exactly where a game wants
        // the cursor when the user is aiming or panning fast — without this,
        // a fast flick to the top of the screen pulled the bar up over the
        // game on every pass. Only checked when hovering would otherwise
        // matter, so it costs nothing the rest of the time.
        if (over && coversFullScreen) over = false;

        var wanted = over || IsActive || _service.HasOpenFlyout || !ShouldHide();
        if (wanted) Reveal(); else Conceal();
    }

    private bool _belowFullscreen;

    /// <summary>
    /// Ducks fully behind whatever fills the screen, and comes back once it
    /// doesn't — each exactly once, not on every Peek tick.
    ///
    /// Concealing alone was not enough to stop the bar from disturbing a game:
    /// the concealed sliver <see cref="Sliver"/> leaves showing is still a
    /// real, topmost window sitting at the very edge of the screen, and
    /// Windows decides which window's cursor applies to a given pixel by
    /// z-order, not by <c>WS_EX_TRANSPARENT</c> — so even click-through, that
    /// sliver kept claiming the system cursor over a game that expects to own
    /// the whole screen, which is what made the real pointer visible over
    /// Body Cam where the game never draws it. Pushing the whole window
    /// behind the one that covers the screen removes it from that decision
    /// entirely: every pixel the cursor can be over now belongs to the
    /// window actually in front of it, because that is what is in front.
    ///
    /// Shares its target state with the <see cref="ABN_FULLSCREENAPP"/>
    /// handler below, which answers the same question for exclusive
    /// fullscreen apps the shell notices on its own; routing both through
    /// this one gate means neither one re-asserts a z-order the other one
    /// just set.
    /// </summary>
    private void SyncFullScreenYield(bool covered)
    {
        if (_hwnd == IntPtr.Zero || _belowFullscreen == covered) return;
        _belowFullscreen = covered;
        SetWindowPos(_hwnd, covered ? HWND_BOTTOM : HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Whether the bar has any reason to be away.
    ///
    /// "Intellihide" — the thing people actually want — is a bar that stays put
    /// on an empty desktop and ducks only when a window would cover it.
    /// Hiding on a bare desktop is the part that feels pointless.
    /// </summary>
    private bool ShouldHide()
    {
        if (_service.Settings.Hiding == HideStyle.Always) return true;
        var r = _shown;
        foreach (var hwnd in Tablet.WindowTools.SwitchableWindows())
        {
            if (Tablet.WindowTools.IsMinimized(hwnd)) continue;
            if (!Tablet.WindowTools.GetWindowRect(hwnd, out var w)) continue;
            if (w.Left < r.Right && w.Right > r.Left && w.Top < r.Bottom && w.Bottom > r.Top) return true;
        }
        return false;
    }

    /// <summary>
    /// True while some window fills this display exactly — an exclusive-
    /// fullscreen app or a borderless one, not merely a window that reaches
    /// under the bar's own strip the way <see cref="ShouldHide"/> checks for.
    ///
    /// This is the borderless case <see cref="ABN_FULLSCREENAPP"/> never
    /// fires for: that notification is the shell's own exclusive-fullscreen
    /// detection, and a borderless game is just an ordinary top-level window
    /// sized to the monitor, so it never sends it. Checked by rect instead,
    /// the same way the taskbar itself has to guess at it.
    /// </summary>
    private bool CoversFullScreen(RECT screen)
    {
        foreach (var hwnd in Tablet.WindowTools.SwitchableWindows())
        {
            if (Tablet.WindowTools.IsMinimized(hwnd)) continue;
            if (!Tablet.WindowTools.GetWindowRect(hwnd, out var w)) continue;
            if (w.Left <= screen.Left && w.Top <= screen.Top && w.Right >= screen.Right && w.Bottom >= screen.Bottom)
                return true;
        }
        return false;
    }

    private void Conceal() => SlideTo(1);

    private void Reveal() => SlideTo(0);

    /// <summary>
    /// Starts the bar moving towards out (0) or away (1).
    ///
    /// Jumping between the two positions was the whole of what the user called
    /// abysmal, and they were right: a dock that teleports reads as a glitch
    /// rather than as something getting out of the way. It is eased over the
    /// setting's duration, and any call part way through simply changes where
    /// it is heading, so a pointer that crosses the edge and comes back does
    /// not stutter.
    /// </summary>
    private void SlideTo(double target)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (Math.Abs(_slideTarget - target) < 0.001)
        {
            // Already heading there, or already sitting there with nothing to
            // do. Peek asks for the same target on every 90 ms tick while
            // nothing changes, and without the second half of this check a
            // "stay put" request restarted a zero-length slide every tick —
            // each one still a real SetWindowPos call, and the reason menus
            // kept ending up under the bar (see PlaceAt).
            var animating = _slide?.IsEnabled ?? false;
            if (animating || Math.Abs(_slidePosition - target) < 0.001) return;
        }
        _slideTarget = target;
        _hidden = target > 0.5;
        SyncClickThrough();

        var duration = Math.Max(0, _service.Settings.HideAnimationMs);
        if (duration == 0 || !IsVisible)
        {
            _slidePosition = target;
            PlaceAt(_slidePosition);
            return;
        }

        if (_slide is null)
        {
            _slide = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _slide.Tick += (_, _) => Step();
        }
        if (!_slide.IsEnabled) _slide.Start();
    }

    /// <summary>
    /// Click-through the instant the bar starts leaving; interactive again the
    /// instant it starts coming back — not once it finishes either trip.
    ///
    /// Concealed, the strip is mostly off-screen with a couple of pixels left
    /// at the edge for <see cref="Peek"/> to reach for, and those pixels used
    /// to be an ordinary hit-testable window like any other. Peek does not
    /// need that: it polls <c>GetCursorPos</c> on a timer, not WPF's mouse
    /// events, so hit-testing was never doing anything for the reveal itself.
    /// All it was doing was giving Windows something to catch the cursor on —
    /// a fast mouse pass over the sliver, the kind a game makes when the user
    /// flicks the view around, could snag on it. WS_EX_TRANSPARENT makes
    /// Win32 skip this window for hit-testing entirely, so the cursor lands on
    /// whatever is actually underneath, same as if the bar were not there.
    /// </summary>
    private void SyncClickThrough()
    {
        if (_hwnd == IntPtr.Zero || _clickThrough == _hidden) return;
        _clickThrough = _hidden;
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        ex = _hidden ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    private void Step()
    {
        var duration = Math.Max(16, _service.Settings.HideAnimationMs);
        var step = 16.0 / duration;

        if (_slidePosition < _slideTarget) _slidePosition = Math.Min(_slideTarget, _slidePosition + step);
        else _slidePosition = Math.Max(_slideTarget, _slidePosition - step);

        PlaceAt(_slidePosition);
        if (Math.Abs(_slidePosition - _slideTarget) < 0.001) _slide?.Stop();
    }

    /// <summary>Puts the window at a point along its travel. 0 is out, 1 is away.</summary>
    private void PlaceAt(double t)
    {
        var r = _shown;
        if (r.Right <= r.Left) return;

        // Eased out: it leaves quickly and arrives gently, which is what makes
        // a slide read as movement rather than as a jump with extra frames.
        var eased = 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

        // How far it has to travel to be off the screen, measured to the
        // screen's edge rather than by its own depth. A floating dock sits a
        // gap clear of the edge, so moving it by its own height left the gap's
        // worth of it still showing — which is what "doesn't go off screen
        // correctly" was.
        var screen = _display.Bounds;
        var travel = _edge switch
        {
            ScreenEdge.Top => r.Bottom - screen.Top,
            ScreenEdge.Bottom => screen.Bottom - r.Top,
            ScreenEdge.Left => r.Right - screen.Left,
            _ => screen.Right - r.Left,
        };
        var off = (int)Math.Round(Math.Max(0, travel - Sliver) * eased);

        var (x, y) = _edge switch
        {
            ScreenEdge.Top => (r.Left, r.Top - off),
            ScreenEdge.Bottom => (r.Left, r.Top + off),
            ScreenEdge.Left => (r.Left - off, r.Top),
            _ => (r.Left + off, r.Top),
        };
        // SWP_NOZORDER, not HWND_TOPMOST: the window is already marked
        // WS_EX_TOPMOST (WPF's Topmost = true, set once at construction), so
        // it stays above every ordinary window regardless. Asking for
        // HWND_TOPMOST here as well does something different and unwanted —
        // it jumps to the very front of the topmost band, ahead of whatever
        // else is topmost. This runs on every slide frame and every 90 ms
        // Peek tick, far more often than the Start menu or a context menu
        // asserts Topmost (once, when it opens), so the bar was winning that
        // race continuously and burying them. Moving without touching z-order
        // is all a slide needs; see ABN_FULLSCREENAPP below for the one place
        // z-order genuinely has to change.
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, r.Right - r.Left, r.Bottom - r.Top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Claims the strip again even if nothing looks different (Explorer restarted, displays changed).</summary>
    public void Redock()
    {
        _docked = default;
        Dock();
    }

    private APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0 && (uint)msg == _callbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED:
                    ScheduleDock();
                    break;
                case ABN_FULLSCREENAPP:
                    // A game or a video covers the bar, as it would the
                    // taskbar. Same gate Peek uses for the borderless case it
                    // finds by rect, so the two do not fight over the z-order.
                    SyncFullScreenYield(lParam != IntPtr.Zero);
                    break;
            }
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_MOUSEACTIVATE)
        {
            // The bar is one of Hearth's non-activating surfaces, so a click on
            // it has to dismiss another program's menu by hand — and one of
            // Hearth's own stacks too, which would otherwise keep its focus and
            // stay open when the user clicked the bar behind it.
            ForeignPopups.DismissForClick();
            _service.CloseStacksExcept(StackUnderPointer());
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    public void CloseForExit()
    {
        _peek?.Stop();
        _peek = null;
        _redock?.Stop();
        if (_registered)
        {
            // Synchronously, and before the window is destroyed. Sent from a
            // background thread it raced the Close below, and Explorer had
            // nothing left to match the handle against — so the strip stayed
            // reserved for ever, and every window on the display kept a gap
            // where a bar used to be. It blocks for a moment; that is the
            // price of the shell letting go.
            var data = NewData();
            SHAppBarMessage(ABM_REMOVE, ref data);
            _registered = false;
        }
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }

    // ---- Interop ---------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    private const uint ABM_NEW = 0;
    private const uint ABM_REMOVE = 1;
    private const uint ABM_SETPOS = 3;
    private const uint ABE_LEFT = 0;
    private const uint ABE_TOP = 1;
    private const uint ABE_RIGHT = 2;
    private const uint ABE_BOTTOM = 3;
    private const int ABN_POSCHANGED = 1;
    private const int ABN_FULLSCREENAPP = 2;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);
}
