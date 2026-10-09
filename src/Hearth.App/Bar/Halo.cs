using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearth.App.Tablet;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;

namespace Hearth.App.Bar;

/// <summary>
/// Halo: the edge of the screen is the bar.
///
/// Every other style here answers "where do I put the strip?". This one
/// refuses the question. A bar costs you a strip of screen for ever in
/// exchange for information you look at for a second at a time, and the usual
/// answers to that are auto-hide (which trades space for a poke at the edge)
/// or a smaller bar (which trades space for squinting). Halo takes the border
/// the screen already has — the pixels outside every maximized window, which
/// nothing has ever used — and makes the whole perimeter one continuous track.
///
/// The perimeter is treated as a single line of length 2(w+h), starting at the
/// top-left and running clockwise, and each running application owns a
/// contiguous arc of it, coloured with its own icon's dominant colour and as
/// long as its share of your open windows. That gives two things a strip
/// cannot:
///
///   - it costs nothing. A ring five pixels thick around the outside is
///     drawn over the screen edge, reserves no work area, and never moves a
///     window;
///   - it is ambient. You are not meant to read it. The colours sit in
///     peripheral vision, always in the same places, and the bright one is
///     whatever you are typing into. You learn "Vivaldi is the orange stretch
///     down the right" the way you learn where a light switch is.
///
/// Reach the pointer to the edge and rest it there, and the arc under it
/// blooms: it thickens and a capsule appears with the icon, the name and the
/// window titles. Click to raise, right-click for the app's menu.
///
/// The dwell matters. The ring is click-through whenever the pointer is only
/// passing, and takes clicks only after the pointer has stayed in the band for
/// a moment, so the close button of a maximized window — which is inside the
/// band — keeps working. Nothing is stolen from a pointer in a hurry.
/// </summary>
internal sealed class Halo : IDisposable
{
    private readonly BarService _service;
    private readonly DisplayTarget _display;
    private readonly Dictionary<ScreenEdge, HaloEdge> _edges = [];
    private readonly DispatcherTimer _pointer;
    private readonly HaloCapsule _capsule;
    private List<HaloArc> _arcs = [];
    private HaloArc? _hot;
    private DateTime _inBandSince = DateTime.MaxValue;
    private bool _clickable;
    private bool _disposed;

    /// <summary>How far past the ring the pointer still counts as being on it.</summary>
    private const double BandPadding = 3;

    /// <summary>How long the pointer has to rest in the band before the ring takes clicks.</summary>
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(180);

    private readonly HaloCorner? _corner;

    public Halo(BarService service, DisplayTarget display)
    {
        _service = service;
        _display = display;
        _capsule = new HaloCapsule();

        foreach (var edge in EnabledEdges()) _edges[edge] = new HaloEdge(this, display, edge);

        // The ring says what is running; it cannot hold a Start button or the
        // notification area. Those go in one small capsule tucked into a
        // corner - the only part of Halo that occupies anything, and it is a
        // corner rather than an edge so it never sits over a window's own
        // controls.
        if (service.Settings.ShowStart || service.Settings.ShowTray || service.Settings.ShowClock)
            _corner = new HaloCorner(service, display);

        // Polled rather than hooked: a low-level mouse hook that runs on every
        // movement is the one thing the shell removes for being slow, and this
        // needs nothing more than where the pointer is. Idle cost is one
        // GetCursorPos every 100 ms; it steps up to about 60 Hz once the
        // pointer is at an edge and something is actually moving.
        _pointer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };
        _pointer.Tick += (_, _) => OnPointer();
        _pointer.Start();

        Refresh();
    }

    public BarService Service => _service;

    private List<ScreenEdge> EnabledEdges()
    {
        var chosen = _service.Settings.HaloEdges;
        if (chosen is { Count: > 0 }) return chosen.Distinct().ToList();
        return [ScreenEdge.Top, ScreenEdge.Right, ScreenEdge.Bottom, ScreenEdge.Left];
    }

    // ---- The track -------------------------------------------------------

    private double Width => (_display.Bounds.Right - _display.Bounds.Left) / _display.Scale;
    private double Height => (_display.Bounds.Bottom - _display.Bounds.Top) / _display.Scale;

    /// <summary>Each enabled edge and where it starts on the perimeter, clockwise from the top-left.</summary>
    private List<(ScreenEdge Edge, double Start, double Length)> Track()
    {
        var order = new[] { ScreenEdge.Top, ScreenEdge.Right, ScreenEdge.Bottom, ScreenEdge.Left };
        var enabled = EnabledEdges();
        var track = new List<(ScreenEdge, double, double)>();
        var at = 0.0;
        foreach (var edge in order)
        {
            if (!enabled.Contains(edge)) continue;
            var length = edge is ScreenEdge.Top or ScreenEdge.Bottom ? Width : Height;
            track.Add((edge, at, length));
            at += length;
        }
        return track;
    }

    private double TotalLength() => Track().Sum(t => t.Length);

    /// <summary>
    /// Divides the perimeter between the applications. A gap is left between
    /// arcs so a run of them still reads as several rather than one long
    /// stripe.
    /// </summary>
    public void Refresh()
    {
        if (_disposed) return;
        try
        {
            var groups = _service.PinnedAndRunning(_service.Windows.Groups);
            var total = TotalLength();
            if (groups.Count == 0 || total <= 0)
            {
                _arcs = [];
                foreach (var edge in _edges.Values) edge.Draw([]);
                return;
            }

            var weighByWindows = _service.Settings.HaloWeighByWindows;
            var weights = groups
                .Select(g => weighByWindows ? Math.Max(1, g.Windows.Count) : 1.0)
                .ToList();
            var sum = weights.Sum();

            var gap = Math.Min(10, total / (groups.Count * 6));
            var usable = total - gap * groups.Count;

            var arcs = new List<HaloArc>();
            var at = gap / 2;
            for (var i = 0; i < groups.Count; i++)
            {
                var length = usable * (weights[i] / sum);
                arcs.Add(new HaloArc(groups[i], at, length, _service.ColourFor(groups[i])));
                at += length + gap;
            }
            _arcs = arcs;

            foreach (var (edge, view) in _edges) view.Draw(SegmentsOn(edge, arcs));
        }
        catch (Exception ex)
        {
            Log.Error("halo refresh", ex);
        }
    }

    /// <summary>
    /// The pieces of each arc that land on one edge. An arc that runs round a
    /// corner is drawn as two pieces, one on each edge, which is what makes
    /// the ring read as continuous.
    /// </summary>
    private List<HaloSegment> SegmentsOn(ScreenEdge edge, List<HaloArc> arcs)
    {
        var entry = Track().FirstOrDefault(t => t.Edge == edge);
        if (entry.Length <= 0) return [];

        var segments = new List<HaloSegment>();
        foreach (var arc in arcs)
        {
            var from = Math.Max(arc.Start, entry.Start);
            var to = Math.Min(arc.Start + arc.Length, entry.Start + entry.Length);
            if (to - from <= 0.5) continue;
            segments.Add(new HaloSegment(arc, from - entry.Start, to - from));
        }
        return segments;
    }

    /// <summary>Where a point on this display falls on the perimeter, or null if it is not near it.</summary>
    private double? PerimeterAt(Point local, double band)
    {
        var width = Width;
        var height = Height;
        var track = Track();

        foreach (var (edge, start, length) in track)
        {
            switch (edge)
            {
                case ScreenEdge.Top when local.Y <= band && local.X >= 0 && local.X <= width:
                    return start + local.X;
                case ScreenEdge.Right when local.X >= width - band && local.Y >= 0 && local.Y <= height:
                    return start + local.Y;
                case ScreenEdge.Bottom when local.Y >= height - band && local.X >= 0 && local.X <= width:
                    return start + (width - local.X);
                case ScreenEdge.Left when local.X <= band && local.Y >= 0 && local.Y <= height:
                    return start + (height - local.Y);
            }
        }
        return null;
    }

    // ---- The pointer -----------------------------------------------------

    private void OnPointer()
    {
        if (_disposed) return;
        if (!GetCursorPos(out var cursor)) return;

        var bounds = _display.Bounds;
        var onThisDisplay = cursor.X >= bounds.Left && cursor.X < bounds.Right &&
                            cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;

        var band = _service.Settings.HaloActiveThickness + BandPadding;
        double? at = null;
        if (onThisDisplay)
        {
            var local = new Point((cursor.X - bounds.Left) / _display.Scale, (cursor.Y - bounds.Top) / _display.Scale);
            at = PerimeterAt(local, band);
        }

        if (at is null)
        {
            _inBandSince = DateTime.MaxValue;
            SetClickable(false);
            SetHot(null);
            _pointer.Interval = TimeSpan.FromMilliseconds(100);
            return;
        }

        _pointer.Interval = TimeSpan.FromMilliseconds(16);
        if (_inBandSince == DateTime.MaxValue) _inBandSince = DateTime.UtcNow;

        var arc = _arcs.FirstOrDefault(a => at >= a.Start && at < a.Start + a.Length);
        SetHot(arc);

        // Only once the pointer has settled: a pointer crossing the band on its
        // way to a maximized window's close button must not lose the click.
        SetClickable(arc is not null && DateTime.UtcNow - _inBandSince >= Dwell);
    }

    private void SetHot(HaloArc? arc)
    {
        if (ReferenceEquals(_hot, arc)) return;
        _hot = arc;
        foreach (var edge in _edges.Values) edge.SetHot(arc);
        if (arc is null) _capsule.HideCapsule();
        else ShowCapsule(arc);
    }

    private void ShowCapsule(HaloArc arc)
    {
        // Put the capsule against the middle of the arc, just inside the ring.
        var middle = arc.Start + arc.Length / 2;
        var entry = Track().FirstOrDefault(t => middle >= t.Start && middle < t.Start + t.Length);
        var local = middle - entry.Start;
        var thickness = _service.Settings.HaloActiveThickness;

        var point = entry.Edge switch
        {
            ScreenEdge.Top => new Point(local, thickness + 6),
            ScreenEdge.Right => new Point(Width - thickness - 6, local),
            ScreenEdge.Bottom => new Point(Width - local, Height - thickness - 6),
            _ => new Point(thickness + 6, Height - local),
        };

        _capsule.ShowFor(arc, _display, point, entry.Edge);
    }

    private void SetClickable(bool clickable)
    {
        if (_clickable == clickable) return;
        _clickable = clickable;
        foreach (var edge in _edges.Values) edge.SetClickable(clickable);
    }

    /// <summary>A click on the ring: raise the app, or walk its windows if it is already in front.</summary>
    public void Click(HaloArc arc, bool menu, FrameworkElement anchor)
    {
        if (menu)
        {
            _service.ShowAppMenu(anchor, arc.Group);
            return;
        }

        var windows = arc.Group.Windows;
        if (windows.Count == 0)
        {
            _service.LaunchAnother(arc.Group);
            return;
        }
        if (windows.Count > 1 && arc.Group.Foreground)
        {
            var current = windows.ToList().FindIndex(w => w.Foreground);
            WindowTools.Activate(windows[(current + 1) % windows.Count].Handle);
            return;
        }
        var window = windows[0];
        if (window.Foreground && !window.Minimized) WindowTools.Minimize(window.Handle);
        else WindowTools.Activate(window.Handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pointer.Stop();
        _capsule.CloseForExit();
        _corner?.CloseForExit();
        foreach (var edge in _edges.Values) edge.CloseForExit();
        _edges.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);
}

/// <summary>One application's stretch of the perimeter.</summary>
internal sealed record HaloArc(BarAppGroup Group, double Start, double Length, Color Colour);

/// <summary>The part of an arc that falls on one edge, in that edge's own coordinates.</summary>
internal sealed record HaloSegment(HaloArc Arc, double Offset, double Length);

/// <summary>
/// One side of the ring: a layered, always-topmost sliver that reserves no
/// space and normally lets every click through it.
/// </summary>
internal sealed class HaloEdge : Window
{
    private readonly Halo _halo;
    private readonly DisplayTarget _display;
    private readonly ScreenEdge _edge;
    private readonly Canvas _canvas = new();
    private readonly Dictionary<HaloArc, Border> _bars = [];
    private IntPtr _hwnd;
    private bool _allowClose;

    private bool Vertical => _edge is ScreenEdge.Left or ScreenEdge.Right;

    public HaloEdge(Halo halo, DisplayTarget display, ScreenEdge edge)
    {
        _halo = halo;
        _display = display;
        _edge = edge;

        Title = "Hearth halo";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _canvas;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT));
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            Place();
        };
        Show();
    }

    private void Place()
    {
        var bounds = _display.Bounds;
        var thickness = (int)Math.Round((_halo.Service.Settings.HaloActiveThickness + 4) * _display.Scale);

        var (x, y, cx, cy) = _edge switch
        {
            ScreenEdge.Top => (bounds.Left, bounds.Top, bounds.Right - bounds.Left, thickness),
            ScreenEdge.Bottom => (bounds.Left, bounds.Bottom - thickness, bounds.Right - bounds.Left, thickness),
            ScreenEdge.Left => (bounds.Left, bounds.Top, thickness, bounds.Bottom - bounds.Top),
            _ => (bounds.Right - thickness, bounds.Top, thickness, bounds.Bottom - bounds.Top),
        };
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, cx, cy, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Draws this edge's pieces of the ring.</summary>
    public void Draw(List<HaloSegment> segments)
    {
        _canvas.Children.Clear();
        _bars.Clear();

        var settings = _halo.Service.Settings;
        var rest = settings.HaloThickness;
        var edgeLength = Vertical
            ? (_display.Bounds.Bottom - _display.Bounds.Top) / _display.Scale
            : (_display.Bounds.Right - _display.Bounds.Left) / _display.Scale;

        foreach (var segment in segments)
        {
            var colour = segment.Arc.Colour;
            var bar = new Border
            {
                CornerRadius = new CornerRadius(rest / 2),
                Background = new SolidColorBrush(colour),
                Opacity = segment.Arc.Group.Windows.Count == 0 ? 0.35
                    : segment.Arc.Group.Foreground ? 1.0 : 0.72,
                ToolTip = segment.Arc.Group.Title,
            };

            // The ring is drawn tight against the outside of the screen; the
            // rest of the window is empty and only there to catch the pointer
            // once it has settled.
            double offset = segment.Offset, length = segment.Length;
            if (_edge == ScreenEdge.Bottom) offset = edgeLength - segment.Offset - length;
            if (_edge == ScreenEdge.Left) offset = edgeLength - segment.Offset - length;

            var thickness = segment.Arc.Group.Foreground ? Math.Max(rest, settings.HaloThickness * 1.6) : rest;
            Size(bar, offset, length, thickness);
            _canvas.Children.Add(bar);
            _bars[segment.Arc] = bar;
        }
    }

    /// <summary>
    /// How thick this window is, in DIPs. Taken from the display rather than
    /// from the Window's own Width, which is never set: the window is placed
    /// with SetWindowPos, so WPF's Width stays at whatever the constructor
    /// left it.
    /// </summary>
    private double Depth => _halo.Service.Settings.HaloActiveThickness + 4;

    private void Size(Border bar, double offset, double length, double thickness)
    {
        if (Vertical)
        {
            bar.Width = thickness;
            bar.Height = length;
            Canvas.SetTop(bar, offset);
            Canvas.SetLeft(bar, _edge == ScreenEdge.Left ? 0 : Depth - thickness);
        }
        else
        {
            bar.Width = length;
            bar.Height = thickness;
            Canvas.SetLeft(bar, offset);
            Canvas.SetTop(bar, _edge == ScreenEdge.Top ? 0 : Depth - thickness);
        }
    }

    /// <summary>Grows the arc the pointer is on, and settles the others back.</summary>
    public void SetHot(HaloArc? arc)
    {
        var settings = _halo.Service.Settings;
        foreach (var (key, bar) in _bars)
        {
            var hot = ReferenceEquals(key, arc);
            var target = hot
                ? settings.HaloActiveThickness
                : key.Group.Foreground ? Math.Max(settings.HaloThickness, settings.HaloThickness * 1.6) : settings.HaloThickness;

            var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            if (Vertical)
            {
                bar.BeginAnimation(WidthProperty, animation);
                if (_edge == ScreenEdge.Right) Canvas.SetLeft(bar, Depth - target);
            }
            else
            {
                bar.BeginAnimation(HeightProperty, animation);
                if (_edge == ScreenEdge.Bottom) Canvas.SetTop(bar, Depth - target);
            }
            bar.Opacity = hot ? 1.0 : key.Group.Windows.Count == 0 ? 0.35 : key.Group.Foreground ? 1.0 : 0.72;
        }
    }

    /// <summary>
    /// Takes the ring in and out of the input path. WS_EX_TRANSPARENT is what
    /// makes a window invisible to the mouse without making it invisible to
    /// the eye, so the ring can be drawn over the edge of a maximized window
    /// and still not interfere with it.
    /// </summary>
    public void SetClickable(bool clickable)
    {
        if (_hwnd == IntPtr.Zero) return;
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        var wanted = clickable ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
        if (wanted != ex) SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(wanted));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOUSEACTIVATE) return IntPtr.Zero;
        Hosting.ForeignPopups.DismissForClick();
        handled = true;
        return new IntPtr(MA_NOACTIVATE);
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (HitArc(e) is { } arc) _halo.Click(arc, menu: false, this);
    }

    protected override void OnMouseRightButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (HitArc(e) is { } arc) _halo.Click(arc, menu: true, this);
        else _halo.Service.ShowBarMenu(this);
    }

    /// <summary>
    /// Which arc the click belongs to. The bars are thinner than the window,
    /// so the click is matched along the edge rather than by what it landed on
    /// — anywhere in the band counts, which is what makes a 5 px target
    /// usable.
    /// </summary>
    private HaloArc? HitArc(System.Windows.Input.MouseEventArgs e)
    {
        var point = e.GetPosition(_canvas);
        var along = Vertical ? point.Y : point.X;
        foreach (var (arc, bar) in _bars)
        {
            var start = Vertical ? Canvas.GetTop(bar) : Canvas.GetLeft(bar);
            var length = Vertical ? bar.Height : bar.Width;
            if (along >= start - 2 && along <= start + length + 2) return arc;
        }
        return null;
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}

/// <summary>
/// What the ring says when you rest on it: the icon, the name and the window
/// titles, in a card that appears just inside the arc.
/// </summary>
internal sealed class HaloCapsule : Window
{
    private readonly StackPanel _content = new();
    private bool _allowClose;

    public HaloCapsule()
    {
        Title = "Hearth halo details";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        IsHitTestVisible = false;
        FontFamily = StartStyle.Body;
        Left = -32000;
        Top = -32000;

        Content = new Border
        {
            Background = BarStyleSheet.Surface,
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 14, 10),
            Child = _content,
        };
        Show();
        Hide();
    }

    public void ShowFor(HaloArc arc, DisplayTarget display, Point localPoint, ScreenEdge edge)
    {
        _content.Children.Clear();

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (arc.Group.Icon is not null)
        {
            var icon = new Image { Source = arc.Group.Icon, Width = 20, Height = 20, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            icon.Margin = new Thickness(0, 0, 9, 0);
            header.Children.Add(icon);
        }
        // A pip in the app's own colour, so the capsule and the arc read as
        // the same thing.
        header.Children.Add(new Border
        {
            Width = 4,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(arc.Colour),
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var name = StartStyle.Label(arc.Group.Program, 13, StartStyle.Text, FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(name);
        _content.Children.Add(header);

        foreach (var window in arc.Group.Windows.Take(6))
        {
            var title = StartStyle.Label(window.Title, 11.5, window.Foreground ? StartStyle.Text : StartStyle.Muted);
            title.Margin = new Thickness(2, 4, 0, 0);
            title.MaxWidth = 300;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            _content.Children.Add(title);
        }
        if (arc.Group.Windows.Count == 0)
            _content.Children.Add(StartStyle.Label("Not running - click to open", 11.5, StartStyle.Faint));

        UpdateLayout();
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var width = DesiredSize.Width;
        var height = DesiredSize.Height;
        var scale = display.Scale;
        var left = display.Bounds.Left + localPoint.X * scale;
        var top = display.Bounds.Top + localPoint.Y * scale;

        // Centred on the arc along the edge, and pushed inwards off it.
        if (edge is ScreenEdge.Top or ScreenEdge.Bottom) left -= width * scale / 2;
        else top -= height * scale / 2;
        if (edge == ScreenEdge.Bottom) top -= height * scale;
        if (edge == ScreenEdge.Right) left -= width * scale;

        left = Math.Clamp(left, display.Bounds.Left + 4, display.Bounds.Right - width * scale - 4);
        top = Math.Clamp(top, display.Bounds.Top + 4, display.Bounds.Bottom - height * scale - 4);

        Left = left / scale;
        Top = top / scale;
        if (!IsVisible) Show();
    }

    public void HideCapsule()
    {
        if (IsVisible) Hide();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }
}

/// <summary>
/// Halo's one concession to a bar: a small capsule in a corner holding Start,
/// the notification area and the clock.
///
/// The ring can carry running applications because they are a list of coloured
/// lengths; it cannot carry a clock or a tray icon, which have to be read. A
/// corner is where those go, because a corner is the part of the screen a
/// maximized window has nothing in.
///
/// It can also get out of the way. The ring's whole claim is that it costs
/// nothing, so the one piece that does occupy space can shrink to a dot until
/// the pointer comes near, or disappear until the pointer reaches the corner —
/// and it can sit in whichever corner is not where the user works.
/// </summary>
internal sealed class HaloCorner : Window
{
    private readonly BarService _service;
    private readonly DisplayTarget _display;
    private readonly Border _surface;
    private readonly FrameworkElement _contents;
    private readonly Border _dot;
    private readonly System.Windows.Threading.DispatcherTimer? _watch;
    private bool _expanded = true;
    private bool _allowClose;

    public HaloCorner(BarService service, DisplayTarget display)
    {
        _service = service;
        _display = display;

        Title = "Hearth halo corner";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = StartStyle.Body;
        Resources = StartStyle.WindowResources();

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (service.Settings.ShowStart)
        {
            var ember = new PressableBorder(radius: 6)
            {
                Padding = new Thickness(7, 2, 7, 2),
                ToolTip = "Start",
                Child = StartStyle.GlyphText(StartStyle.Glyph(0xECAD), 13, StartStyle.Accent),
            };
            ember.Clicked += _ => service.OpenStart();
            ember.RightClicked += _ => service.ShowBarMenu(ember);
            row.Children.Add(ember);
        }
        if (service.Settings.ShowTray) row.Children.Add(new BarTray(false, 14));
        row.Children.Add(ControlCentreLauncher());
        if (service.Settings.ShowClock) row.Children.Add(new BarClock(false, showDate: false));
        _contents = row;

        // What is left when it shrinks: a dot in the app's own accent, so the
        // corner still says Hearth is there.
        _dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = StartStyle.Accent,
            Margin = new Thickness(3),
            Visibility = Visibility.Collapsed,
        };

        _surface = new Border
        {
            Background = BarStyleSheet.Surface,
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(service.Settings.CornerRadius >= 0 ? service.Settings.CornerRadius : 9),
            Padding = new Thickness(3),
            Child = new Grid { Children = { _contents, _dot } },
        };
        _surface.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            service.ShowBarMenu(_surface);
        };

        var scale = Math.Clamp(service.Settings.PillScale, 0.6, 1.6);
        Content = Math.Abs(scale - 1) < 0.01
            ? _surface
            : new Viewbox { Child = _surface, Stretch = Stretch.Uniform, LayoutTransform = new ScaleTransform(scale, scale) };

        if (service.Settings.Pill != PillSize.Full)
        {
            SetExpanded(false, animate: false);
            _watch = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(110),
            };
            _watch.Tick += (_, _) => Watch();
            _watch.Start();
        }

        Loaded += (_, _) => Place();
        SizeChanged += (_, _) => Place();
        Show();
    }

    /// <summary>Windows' own quick settings; see the note on the bars' version.</summary>
    private static FrameworkElement ControlCentreLauncher()
    {
        var button = new PressableBorder(radius: 6)
        {
            Padding = new Thickness(6, 2, 6, 2),
            ToolTip = "Quick settings",
            Child = StartStyle.GlyphText(StartStyle.Glyph(0xE9E9), 13, StartStyle.Text),
        };
        button.Clicked += _ => WindowTools.SendWinChord('A');
        return button;
    }

    /// <summary>Full or shrunk, depending on how near the pointer is to the corner.</summary>
    private void Watch()
    {
        if (!GetCursorPos(out var cursor)) return;
        if (!GetWindowRect(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var rect)) return;

        // A generous reach, because a shrunk pill is a small target.
        var reach = (int)Math.Round(60 * _display.Scale);
        var near = cursor.X >= rect.Left - reach && cursor.X <= rect.Right + reach &&
                   cursor.Y >= rect.Top - reach && cursor.Y <= rect.Bottom + reach;

        if (near != _expanded) SetExpanded(near, animate: true);
    }

    /// <summary>
    /// Opens the pill out of its dot, or folds it back.
    ///
    /// Swapping the two in one frame read as the dot being replaced by
    /// something else rather than as the same thing unfolding. The contents
    /// are kept measured while they fade and scale up from the dot's end — the
    /// end nearest the corner, so it grows out of where the dot was rather than
    /// from its own middle — and the dot crosses the other way.
    /// </summary>
    private void SetExpanded(bool expanded, bool animate)
    {
        _expanded = expanded;
        var hidden = _service.Settings.Pill == PillSize.Hidden;

        _contents.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _dot.Visibility = expanded || hidden ? Visibility.Collapsed : Visibility.Visible;
        _surface.Visibility = expanded || !hidden ? Visibility.Visible : Visibility.Collapsed;

        if (!animate) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(expanded ? 170 : 130);

        // Grown from the corner the pill lives in, so it unfolds towards the
        // middle of the screen instead of away from it.
        var fromRight = _service.Settings.PillCorner is Corner.TopRight or Corner.BottomRight;
        _surface.RenderTransformOrigin = new Point(fromRight ? 1 : 0, 0.5);

        var scale = new ScaleTransform(expanded ? 0.4 : 1, expanded ? 0.7 : 1);
        _surface.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(expanded ? 0.4 : 1, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(expanded ? 0.7 : 1, 1, duration) { EasingFunction = ease });

        _surface.BeginAnimation(OpacityProperty,
            new DoubleAnimation(expanded ? 0.3 : 1, 1, duration) { EasingFunction = ease });

        if (expanded)
        {
            _contents.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        }
    }

    /// <summary>In its chosen corner, clear of the ring.</summary>
    private void Place()
    {
        var scale = _display.Scale;
        var margin = Math.Max(0, _service.Settings.PillMargin) * scale;
        var width = ActualWidth * scale;
        var height = ActualHeight * scale;
        if (width <= 0 || height <= 0) return;

        var bounds = _display.Bounds;
        var left = _service.Settings.PillCorner is Corner.TopLeft or Corner.BottomLeft
            ? bounds.Left + margin
            : bounds.Right - width - margin;
        var top = _service.Settings.PillCorner is Corner.TopLeft or Corner.TopRight
            ? bounds.Top + margin
            : bounds.Bottom - height - margin;

        Left = left / scale;
        Top = top / scale;
    }

    public void CloseForExit()
    {
        _watch?.Stop();
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
