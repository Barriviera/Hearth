using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hearth.App.Tablet;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;

namespace Hearth.App.Bar;

/// <summary>One display a bar can be placed on.</summary>
internal sealed record DisplayTarget(string Name, BarWindow.RECT Bounds, double Scale, bool Primary, IntPtr Handle);

/// <summary>The displays, in the same primary-first order the home screen uses.</summary>
internal static class Displays
{
    public static List<DisplayTarget> All()
    {
        var found = new List<DisplayTarget>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
                found.Add(new DisplayTarget(info.szDevice, info.rcMonitor, scale, (info.dwFlags & MONITORINFOF_PRIMARY) != 0, monitor));
            }
            return true;
        }, IntPtr.Zero);

        return found
            .OrderByDescending(m => m.Primary)
            .ThenBy(m => m.Bounds.Top)
            .ThenBy(m => m.Bounds.Left)
            .ToList();
    }

    private const int MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public BarWindow.RECT rcMonitor;
        public BarWindow.RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}

/// <summary>Bar colours, kept apart from Start's so a bar can be tuned on its own.</summary>
internal static class BarStyleSheet
{
    public static Brush Surface => StartStyle.IsLight
        ? Frozen(Color.FromArgb(0xF2, 0xF3, 0xF3, 0xF6))
        : Frozen(Color.FromArgb(0xF2, 0x18, 0x18, 0x1D));

    /// <summary>The line under a running app's button.</summary>
    public static Brush Running => StartStyle.Accent;

    public static Brush Idle => StartStyle.Muted;

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// A window (or an application) as a button on a bar. Every style draws these
/// differently, so the button takes its own look as arguments rather than
/// deciding for itself.
/// </summary>
internal sealed class BarButton : PressableBorder
{
    private readonly BarAppGroup _group;
    private readonly BarService _service;
    private readonly Border _indicator;

    public BarButton(BarService service, BarAppGroup group, double iconSize, bool showTitle,
        ScreenEdge edge, double radius = 8)
        : base(radius: radius)
    {
        var vertical = edge is ScreenEdge.Left or ScreenEdge.Right;
        _service = service;
        _group = group;

        // Through Hearth's own icon pipeline, so the bar matches the home
        // screen rather than showing whatever 16 px the program gave the shell.
        var icon = BarIcons.Build(group, iconSize, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        icon.VerticalAlignment = VerticalAlignment.Center;

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        if (showTitle)
        {
            var label = StartStyle.Label(group.Title, 12, group.Foreground ? StartStyle.Text : StartStyle.Muted);
            label.Margin = new Thickness(8, 0, 4, 0);
            label.MaxWidth = 150;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
        }

        // The running indicator: a short line that lengthens with the number of
        // windows, so grouped buttons still say how much is behind them.
        //
        // It sits against whichever side of the button faces the edge of the
        // screen — the bottom on a bar along the bottom, the top on one along
        // the top — because that is the side with nothing else on it. Flush
        // against it, too: a gap only pushes it back into the label.
        // Small. It is a hint, not a label: a dot for something running, a
        // short bar for whatever is in front. Growing it with the window count
        // made it the loudest thing on the button, and the count is in the
        // tooltip where it belongs.
        var run = group.Foreground ? 14.0 : 4.0;
        _indicator = new Border
        {
            Background = group.Foreground ? BarStyleSheet.Running : BarStyleSheet.Idle,
            CornerRadius = new CornerRadius(1.25),
            Height = vertical ? run : 2.5,
            Width = vertical ? 2.5 : run,
            HorizontalAlignment = edge switch
            {
                ScreenEdge.Left => HorizontalAlignment.Left,
                ScreenEdge.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center,
            },
            VerticalAlignment = edge switch
            {
                ScreenEdge.Top => VerticalAlignment.Top,
                ScreenEdge.Bottom => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Center,
            },
            Opacity = group.Windows.Count == 0 ? 0 : group.Minimized ? 0.4 : group.Foreground ? 1 : 0.6,
            IsHitTestVisible = false,
        };

        // The content is inset on the indicator's side so the label and the
        // icon keep clear of it.
        var lead = showTitle ? 8.0 : 6.0;
        Padding = edge switch
        {
            ScreenEdge.Top => new Thickness(lead, 6, lead, 3),
            ScreenEdge.Bottom => new Thickness(lead, 3, lead, 6),
            ScreenEdge.Left => new Thickness(7, 4, 4, 4),
            _ => new Thickness(4, 4, 7, 4),
        };
        Margin = new Thickness(vertical ? 0 : 1, vertical ? 1 : 0, vertical ? 0 : 1, vertical ? 1 : 0);
        Background = group.Foreground ? StartStyle.Selected : Brushes.Transparent;
        ToolTip = group.Windows.Count == 1 ? group.Windows[0].Title : $"{group.Program} — {group.Windows.Count} windows";

        Child = new Grid { Children = { row, _indicator } };

        Clicked += _ => Activate();
        RightClicked += _ => ShowMenu();
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            _service.LaunchAnother(group);
        };
    }

    /// <summary>
    /// A click on the app that is already in front minimizes it, which is what
    /// the Windows taskbar does; with several windows it moves to the next one
    /// instead of minimizing the lot.
    /// </summary>
    private void Activate()
    {
        var windows = _group.Windows;
        if (windows.Count == 0) return;

        if (windows.Count > 1 && _group.Foreground)
        {
            var current = windows.ToList().FindIndex(w => w.Foreground);
            WindowTools.Activate(windows[(current + 1) % windows.Count].Handle);
            return;
        }

        var window = windows[0];
        if (window.Foreground && !window.Minimized) WindowTools.Minimize(window.Handle);
        else WindowTools.Activate(window.Handle);
    }

    private void ShowMenu() => _service.ShowAppMenu(this, _group);
}

/// <summary>
/// The notification area on a Hearth bar: the icons Windows itself is showing,
/// read through UI Automation.
///
/// Hearth does not own these icons, and there is no supported way to take them
/// over — an application registers its icon with Explorer's tray. So the tray
/// here is a view onto the real one: it reads what is there, draws it at
/// Hearth's size, and hands clicks and right-clicks straight back to the real
/// icon. With the Windows taskbar hidden by region (see
/// <see cref="ShellTaskbar"/>) that read is quick and nothing flashes, which is
/// what made this worth doing at all.
/// </summary>
internal sealed class BarTray : StackPanel
{
    private readonly bool _vertical;
    private readonly double _size;
    private readonly DispatcherTimer _refresh;
    private bool _showHidden;
    private bool _reading;
    private bool _readHiddenOnce;
    private bool _read;
    private List<TrayIcon> _shown = [];
    private List<TrayIcon> _hidden = [];

    public BarTray(bool vertical, double size)
    {
        _vertical = vertical;
        _size = size;
        Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;

        // Tray icons change their own images and tooltips (Task Manager's shows
        // live CPU use), so the shown ones are re-read now and then. The hidden
        // ones are not: see ReadAsync.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _refresh.Tick += (_, _) => _ = ReadAsync(includeHidden: false);
        // Loaded fires again every time the bar rebuilds and moves this panel
        // into the new tree, which is whenever any window anywhere changes.
        // Reading the real notification area that often is exactly what the
        // timer was made careful about in the first place.
        Loaded += (_, _) =>
        {
            _refresh.Start();
            if (_read) return;
            _read = true;
            _ = ReadAsync(includeHidden: false);
        };
        Unloaded += (_, _) => _refresh.Stop();
    }

    /// <summary>
    /// Reads the notification area.
    ///
    /// <paramref name="includeHidden"/> is the expensive, disruptive half:
    /// the hidden icons only exist while Explorer's overflow flyout is open,
    /// and opening it means invoking the real chevron, which activates the
    /// flyout. On a timer that lands on the user every time — the report was
    /// "the hidden tasks are being opened periodically, ripping me out of
    /// whatever I'm doing". So nothing on a timer ever asks for them: the
    /// hidden icons are read once, when the user opens them here, and the
    /// foreground is put back either way (see ForegroundGuard).
    /// </summary>
    public async Task ReadAsync(bool includeHidden)
    {
        if (_reading) return;
        _reading = true;
        try
        {
            var icons = await TrayIcons.ReadAsync(includeHidden).ConfigureAwait(true);
            _shown = icons.Where(i => !i.Hidden).ToList();
            if (includeHidden)
            {
                _hidden = icons.Where(i => i.Hidden).ToList();
                _readHiddenOnce = true;
            }
            Build();
            // Reading leaves Explorer's own tooltip behind when the taskbar it
            // belongs to has no area to take it away again.
            ShellTaskbar.DismissOrphanedPopups();
        }
        catch (Exception ex)
        {
            Log.Error("bar: tray", ex);
        }
        finally
        {
            _reading = false;
        }
    }

    private void Build()
    {
        Children.Clear();

        // The chevron is offered until a full read proves there is nothing
        // behind it, because finding that out is the very thing that costs.
        if (!_readHiddenOnce || _hidden.Count > 0)
        {
            var chevron = new PressableBorder(radius: 5)
            {
                Padding = new Thickness(4),
                ToolTip = _showHidden ? "Hide these icons"
                    : _readHiddenOnce ? $"{_hidden.Count} more icons" : "More icons",
                Child = StartStyle.GlyphText(StartStyle.Glyph(_showHidden ? 0xE70D : 0xE70E), 10, StartStyle.Muted),
            };
            chevron.Clicked += _ => ToggleHidden();
            Children.Add(chevron);
        }

        foreach (var icon in (_showHidden ? _hidden.Concat(_shown) : _shown))
            Children.Add(Tile(icon));
    }

    private async void ToggleHidden()
    {
        _showHidden = !_showHidden;
        Build();
        // The first look costs one flyout; after that the list is kept and
        // refreshed only when it is opened again.
        if (_showHidden) await ReadAsync(includeHidden: true).ConfigureAwait(true);
    }

    private UIElement Tile(TrayIcon icon)
    {
        FrameworkElement content = icon.Image is not null
            ? new Image { Source = icon.Image, Width = _size, Height = _size, Stretch = Stretch.Uniform }
            : StartStyle.GlyphText(StartStyle.Glyph(0xE783), _size * 0.8, StartStyle.Muted);

        var tile = new PressableBorder(radius: 5)
        {
            Padding = new Thickness(4),
            ToolTip = icon.Title,
            Child = content,
            Margin = new Thickness(_vertical ? 0 : 1, _vertical ? 1 : 0, _vertical ? 0 : 1, _vertical ? 1 : 0),
        };
        tile.Clicked += _ => Activate(icon, menu: false);
        tile.RightClicked += _ => Activate(icon, menu: true);
        return tile;
    }

    /// <summary>
    /// Clicking the real icon takes a moment (the flyout may have to be
    /// opened), so it is not waited on; a failure is logged rather than thrown
    /// into the click handler. This one is the user's own doing, so the
    /// foreground is left wherever the app puts it.
    /// </summary>
    private static async void Activate(TrayIcon icon, bool menu)
    {
        try
        {
            await TrayIcons.ActivateAsync(icon, menu).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error($"bar: tray '{icon.Name}'", ex);
        }
    }
}

/// <summary>The clock, which is also the way to the notification centre.</summary>
internal sealed class BarClock : PressableBorder
{
    private readonly TextBlock _text;
    private readonly DispatcherTimer _timer;

    public BarClock(bool vertical, bool showDate = true) : base(radius: 6)
    {
        Padding = new Thickness(vertical ? 4 : 10, 3, vertical ? 4 : 10, 3);
        VerticalAlignment = VerticalAlignment.Center;
        _text = new TextBlock
        {
            FontFamily = StartStyle.Body,
            FontSize = vertical ? 11 : 12,
            Foreground = StartStyle.Text,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = vertical ? 13 : 14,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        Child = _text;
        Clicked += _ => WindowTools.SendWinChord('N');

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Update(vertical, showDate);
        Loaded += (_, _) => { Update(vertical, showDate); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Update(bool vertical, bool showDate)
    {
        var now = DateTime.Now;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var time = now.ToString("t", culture);
        if (!showDate) { _text.Text = vertical ? time.Replace(" ", Environment.NewLine) : time; return; }
        _text.Text = vertical
            ? time.Replace(" ", Environment.NewLine)
            : time + Environment.NewLine + now.ToString("d", culture);
    }
}
