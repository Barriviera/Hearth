using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearth.App.Services;
using Hearth.App.Tablet;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;
using Hearth.Core.Shell;

namespace Hearth.App.Bar;

/// <summary>
/// Hearth's optional bar.
///
/// Optional is the point: Hearth has always run alongside the Windows taskbar
/// and still does. Nothing here starts until the setting is switched on, and
/// switching it off puts the Windows taskbar back exactly as it was. What the
/// bar offers instead is five genuinely different answers to what a bar is —
/// see <see cref="BarStyle"/> — rather than one bar with five skins.
///
/// One service owns all of it: the settings, the shared window list, a window
/// (or a halo) per display, and whatever it did to the Windows taskbar. Every
/// style draws from the same window list, so five presentations never run five
/// EnumWindows loops of their own.
/// </summary>
internal sealed class BarService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly List<BarWindow> _windows = [];
    private readonly List<BarPresentation> _presentations = [];
    private readonly List<Halo> _halos = [];
    private readonly Dictionary<string, Color> _colours = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _enforce;
    private DispatcherTimer? _rebuild;
    private bool _disposed;

    public static BarService? Current { get; private set; }

    public BarSettings Settings { get; private set; }

    public WindowList Windows { get; }

    public int BarCount => _windows.Count + _halos.Count;

    public event Action? Changed;

    public BarService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Current = this;
        Settings = BarSettings.Load();
        Windows = new WindowList(dispatcher);
        Windows.Changed += OnWindowsChanged;

        // Explorer drops the taskbar's region whenever it rebuilds the bar, and
        // it does that for reasons Hearth never hears about, so it is put back
        // on a timer as well as after every event we do hear.
        _enforce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _enforce.Tick += (_, _) =>
        {
            ShellTaskbar.Apply();
            ShellTaskbar.DismissOrphanedPopups();
            // The hidden taskbar keeps reserving its strip until Explorer
            // reloads, so the work area is taken back here rather than the user
            // being asked to restart anything.
            ShellTaskbar.ReclaimStrips(ReservedStrips());
        };

        if (Settings.Enabled) Start();
    }

    // ---- Lifetime --------------------------------------------------------

    public bool IsRunning => _windows.Count > 0 || _halos.Count > 0;

    public void Start()
    {
        if (IsRunning) return;
        try
        {
            // A previous run that was killed, or a style change before the
            // release was made synchronous, can leave a strip reserved that
            // nothing occupies. The live bars reclaim theirs straight after.
            ShellTaskbar.ResetWorkArea();
            if (Settings.ShellTaskbar == ShellTaskbarHandling.Hide)
            {
                ShellTaskbar.Hide();
                _enforce.Start();
                Watchdog.Start();
            }
            Build();
            Log.Write($"bar: started, style {Settings.Style}, edge {Settings.Edge}, {BarCount} on screen");
        }
        catch (Exception ex)
        {
            Log.Error("bar start", ex);
            Stop();
        }
    }

    public void Stop()
    {
        _enforce.Stop();
        Tear();
        ShellTaskbar.ResetWorkArea();
        if (ShellTaskbar.IsHiddenByUs)
        {
            ShellTaskbar.Restore();
            Watchdog.StopAfter(() => { });
        }
        Log.Write("bar: stopped");
    }

    public void SetEnabled(bool enabled)
    {
        Settings.Enabled = enabled;
        Settings.Save();
        if (enabled) Start(); else Stop();
        Changed?.Invoke();
    }

    /// <summary>Saves the settings and rebuilds everything the change could touch.</summary>
    private bool _applying;
    private DispatcherTimer? _applyAgain;

    /// <summary>
    /// Saves the settings and rebuilds the bars.
    ///
    /// Guarded, because a rebuild tears down every bar and puts up new ones,
    /// and anything that re-entered it would do that several times a second —
    /// which the user hit once and could not get out of, because the settings
    /// window it had to be turned off from was being rebuilt underneath them.
    /// A call that arrives while one is in progress is coalesced into a single
    /// follow-up rather than run immediately, so however the re-entry happens
    /// it costs one extra rebuild and not a storm.
    /// </summary>
    public void SaveAndApply()
    {
        if (_applying)
        {
            _applyAgain ??= NewApplyTimer();
            _applyAgain.Stop();
            _applyAgain.Start();
            return;
        }

        _applying = true;
        try { ApplyNow(); }
        finally { _applying = false; }
    }

    private DispatcherTimer NewApplyTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SaveAndApply();
        };
        return timer;
    }

    private void ApplyNow()
    {
        Settings.Save();
        if (!Settings.Enabled)
        {
            Stop();
            Changed?.Invoke();
            return;
        }

        if (Settings.ShellTaskbar == ShellTaskbarHandling.Hide && !ShellTaskbar.IsHiddenByUs)
        {
            ShellTaskbar.Hide();
            _enforce.Start();
            Watchdog.Start();
        }
        else if (Settings.ShellTaskbar == ShellTaskbarHandling.Leave && ShellTaskbar.IsHiddenByUs)
        {
            _enforce.Stop();
            ShellTaskbar.Restore();
        }

        Tear();
        // Explorer can hold on to a strip whose app bar has gone (switching
        // away from the menu bar left 34 px reserved with nothing in it), so
        // the work area is handed back before the new bars claim theirs.
        ShellTaskbar.ResetWorkArea();
        Build();
        Changed?.Invoke();
    }

    private void Tear()
    {
        CloseStacks();
        foreach (var halo in _halos) halo.Dispose();
        _halos.Clear();
        _presentations.Clear();
        foreach (var window in _windows) window.CloseForExit();
        _windows.Clear();
    }

    private void Build()
    {
        var displays = Displays.All();
        if (!Settings.AllDisplays) displays = displays.Where(d => d.Primary).ToList();
        if (displays.Count == 0) return;

        var (min, max, _) = Settings.ThicknessRange();
        var thickness = Math.Clamp(Settings.Thickness, min, max);

        foreach (var display in displays)
        {
            if (Settings.Style == BarStyle.Halo)
            {
                _halos.Add(new Halo(this, display));
                continue;
            }

            switch (Settings.Style)
            {
                case BarStyle.MenuBarAndDock:
                    // Two bars: the menu bar reserves its strip at the top, the
                    // dock floats at the opposite edge over the windows. Each
                    // hides on its own, because wanting the menu bar always
                    // there and the dock out of the way is the usual pairing.
                    Add(new BarWindow(this, display, ScreenEdge.Top, Math.Min(thickness, 34), BarDock.Strip,
                            reserveSpace: !Settings.MenuBarAutoHide),
                        w => new MenuBarPresentation(this, w), Settings.MenuBarAutoHide);
                    Add(new BarWindow(this, display, Opposite(ScreenEdge.Top), thickness * 1.5, BarDock.Floating, reserveSpace: false),
                        w => new DockPresentation(this, w), Settings.DockAutoHide);
                    break;

                case BarStyle.Panel:
                    Add(new BarWindow(this, display, Settings.Edge, thickness, BarDock.Strip, reserveSpace: !Settings.AutoHide),
                        w => new PanelPresentation(this, w), Settings.AutoHide);
                    break;

                case BarStyle.Docklike:
                    Add(new BarWindow(this, display, Settings.Edge, thickness, BarDock.Strip, reserveSpace: !Settings.AutoHide),
                        w => new DocklikePresentation(this, w), Settings.AutoHide);
                    break;

                default:
                    Add(new BarWindow(this, display, Settings.Edge, thickness, BarDock.Strip, reserveSpace: !Settings.AutoHide),
                        w => new TaskbarPresentation(this, w), Settings.AutoHide);
                    break;
            }
        }

        RefreshAll();
    }

    private void Add(BarWindow window, Func<BarWindow, BarPresentation> make, bool autoHide = false)
    {
        _windows.Add(window);
        _presentations.Add(make(window));
        window.Show();
        if (autoHide) window.EnableAutoHide(true);
    }

    private static ScreenEdge Opposite(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Top => ScreenEdge.Bottom,
        ScreenEdge.Bottom => ScreenEdge.Top,
        ScreenEdge.Left => ScreenEdge.Right,
        _ => ScreenEdge.Left,
    };

    // ---- Content ---------------------------------------------------------

    private void OnWindowsChanged()
    {
        // The window list can change several times in a burst (an app opening
        // shows a splash, then a window, then renames it); rebuilding the
        // buttons once at the end of that is both cheaper and less flickery.
        _rebuild ??= NewRebuildTimer();
        _rebuild.Stop();
        _rebuild.Start();
    }

    private DispatcherTimer NewRebuildTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        timer.Tick += (_, _) => { timer.Stop(); RefreshAll(); };
        return timer;
    }

    public void RefreshAll()
    {
        foreach (var presentation in _presentations)
        {
            try { presentation.Refresh(); }
            catch (Exception ex) { Log.Error("bar refresh", ex); }
        }
        foreach (var halo in _halos)
        {
            try { halo.Refresh(); }
            catch (Exception ex) { Log.Error("halo refresh", ex); }
        }
    }

    /// <summary>
    /// The edge a docked Hearth bar holds on a display, for anything that has
    /// to stand on it — the Start menu, chiefly. Halo and the floating dock do
    /// not count: neither reserves anything, so nothing needs to avoid them.
    /// </summary>
    public ScreenEdge? EdgeFor(IntPtr monitor)
    {
        foreach (var window in _windows)
        {
            if (window.Display.Handle != monitor) continue;
            if (!window.ReservesSpace) continue;
            return window.Edge;
        }
        return null;
    }

    /// <summary>
    /// The margin, in DIPs, Hearth's own home screen should leave clear on a
    /// display for a bar that reserves no space there.
    ///
    /// An auto-hiding bar deliberately skips the shell reservation — that is
    /// what keeps every other window from resizing each time it hides — but
    /// the home screen is not "every other window," it is Hearth's own layer,
    /// and the bar is a real strip of pixels the moment intellihide reveals
    /// it. Without this, the grid laid tiles out to the bare edge and an
    /// intellihide reveal covered whatever landed there. Floating docks are
    /// excluded: they sit a gap clear of the edge over the desktop on
    /// purpose, the same as they sit over any other window.
    /// </summary>
    public (ScreenEdge Edge, double Thickness)? UnreservedMarginFor(IntPtr monitor)
    {
        foreach (var window in _windows)
        {
            if (window.Display.Handle != monitor) continue;
            if (window.Floating || window.ReservesSpace) continue;
            return (window.Edge, window.Thickness);
        }
        return null;
    }

    /// <summary>
    /// The edge a docked Hearth bar sits on, whether or not it reserves space.
    ///
    /// <see cref="EdgeFor"/> is about the work area and so ignores a bar that
    /// hides; the Start menu wants the other answer. A menu should still come
    /// from the edge its bar lives on even when that bar is currently away —
    /// that is where the Start button is, and where the user just clicked.
    /// </summary>
    public ScreenEdge? BarEdgeFor(IntPtr monitor)
    {
        foreach (var window in _windows)
        {
            if (window.Display.Handle != monitor) continue;
            if (window.Floating) continue;
            return window.Edge;
        }
        return null;
    }

    /// <summary>
    /// Where Hearth's own Start button is on a display, in screen pixels, or
    /// null when this bar has none. Used to line the menu up with it.
    /// </summary>
    public Rect? StartButtonRect(IntPtr monitor)
    {
        foreach (var window in _windows)
        {
            if (window.Display.Handle != monitor) continue;
            if (window.StartButton is not { IsLoaded: true } button) continue;
            try
            {
                var origin = button.PointToScreen(new Point(0, 0));
                var scale = window.Display.Scale;
                return new Rect(origin.X, origin.Y, button.ActualWidth * scale, button.ActualHeight * scale);
            }
            catch (InvalidOperationException)
            {
                // Not in a visual tree yet.
            }
        }
        return null;
    }

    /// <summary>
    /// The strips Hearth's own bars are claiming. An auto-hiding bar claims
    /// nothing — it is not occupying the space it is hidden from — so it is not
    /// listed here either.
    /// </summary>
    public IEnumerable<(int Left, int Top, int Right, int Bottom)> ReservedStrips() =>
        _windows.Where(w => w.ReservesSpace).Select(w => w.DockedStrip);

    /// <summary>Explorer restarted: its taskbar is new, so the region and the strips go back on.</summary>
    public void OnExplorerRestarted()
    {
        if (!IsRunning) return;
        ShellTaskbar.Apply();
        foreach (var window in _windows) window.Redock();
    }

    public void OnDisplaysChanged()
    {
        if (!IsRunning) return;
        Tear();
        Build();
    }

    /// <summary>The pinned apps and the unpinned running ones, each in a stable order.</summary>
    public readonly record struct BarItems(IReadOnlyList<BarAppGroup> Pinned, IReadOnlyList<BarAppGroup> Running)
    {
        public IEnumerable<BarAppGroup> All => Pinned.Concat(Running);
        public bool HasBoth => Pinned.Count > 0 && Running.Count > 0;
    }

    /// <summary>When each program was first seen running, so the order is launch order.</summary>
    private readonly Dictionary<string, int> _firstSeen = new(StringComparer.OrdinalIgnoreCase);
    private int _sequence;

    /// <summary>
    /// What a bar shows, in the order every dock and taskbar uses: the pinned
    /// apps in the order they were pinned, then everything else in the order it
    /// was opened, appended at the end.
    ///
    /// The order has to be stable or the bar is unusable. The window list
    /// arrives in Z-order, so sorting by it would move a button every time you
    /// switched windows — the thing you were about to click would not be where
    /// you looked. Each program's place is therefore fixed when it first
    /// appears and kept until it closes.
    /// </summary>
    public BarItems ItemsFor(IReadOnlyList<BarAppGroup> running)
    {
        var pinned = new List<BarAppGroup>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pin in Settings.Pinned)
        {
            var match = running.FirstOrDefault(g =>
                g.ProgramPath.Equals(pin, StringComparison.OrdinalIgnoreCase) ||
                g.Program.Equals(pin, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                pinned.Add(match with { Pinned = true });
                claimed.Add(match.ProgramPath);
            }
            else
            {
                // Pinned but not running: an empty group, which every style
                // already knows how to draw faintly.
                pinned.Add(new BarAppGroup(pin, PinnedName(pin), []) { Pinned = true });
            }
        }

        var rest = running.Where(g => !claimed.Contains(g.ProgramPath)).ToList();

        // Programs that have gone are forgotten, so a later run of the same one
        // joins at the end rather than reclaiming an old place.
        var live = new HashSet<string>(rest.Select(Key), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _firstSeen.Keys.Where(k => !live.Contains(k)).ToList()) _firstSeen.Remove(gone);

        foreach (var group in rest)
        {
            var key = Key(group);
            if (!_firstSeen.ContainsKey(key)) _firstSeen[key] = ++_sequence;
        }

        rest.Sort((a, b) => _firstSeen[Key(a)].CompareTo(_firstSeen[Key(b)]));
        return new BarItems(pinned, rest);

        static string Key(BarAppGroup g) => g.ProgramPath.Length > 0 ? g.ProgramPath : g.Program;
    }

    /// <summary>Everything a bar shows, flattened, for the styles that draw one run.</summary>
    public IReadOnlyList<BarAppGroup> PinnedAndRunning(IReadOnlyList<BarAppGroup> running) =>
        ItemsFor(running).All.ToList();

    private static string PinnedName(string pin)
    {
        var app = App.Apps.Find(pin);
        if (app is not null) return app.DisplayName;
        try { return System.IO.Path.GetFileNameWithoutExtension(pin); }
        catch (ArgumentException) { return pin; }
    }

    public void Pin(BarAppGroup group)
    {
        var key = group.ProgramPath.Length > 0 ? group.ProgramPath : group.Program;
        if (Settings.Pinned.Contains(key, StringComparer.OrdinalIgnoreCase)) return;
        Settings.Pinned.Add(key);
        Settings.Save();
        RefreshAll();
    }

    public void Unpin(BarAppGroup group)
    {
        var key = group.ProgramPath.Length > 0 ? group.ProgramPath : group.Program;
        var removed = Settings.Pinned.RemoveAll(p => p.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        Settings.Save();
        RefreshAll();
    }

    public bool IsPinned(BarAppGroup group) =>
        Settings.Pinned.Contains(group.ProgramPath, StringComparer.OrdinalIgnoreCase) ||
        Settings.Pinned.Contains(group.Program, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The colour Halo paints an application in: the dominant colour of its
    /// own icon, so the ring is coloured by the things you recognise rather
    /// than by a palette nobody chose.
    /// </summary>
    public Color ColourFor(BarAppGroup group)
    {
        var key = group.ProgramPath.Length > 0 ? group.ProgramPath : group.Program;
        if (_colours.TryGetValue(key, out var cached)) return cached;

        var colour = group.Icon is BitmapSource bitmap ? Dominant(bitmap) : StableColour(key);
        _colours[key] = colour;
        return colour;
    }

    /// <summary>
    /// The icon's own colour, read from a 16x16 copy of it.
    ///
    /// Averaging every pixel gives mud, because most icons are mostly one flat
    /// background; the most saturated pixels are the ones a person would call
    /// "the colour of that icon", so those are what count. A weight of
    /// saturation cubed is enough to let a small bright mark beat a large dull
    /// field without a single pixel deciding it on its own.
    /// </summary>
    private static Color Dominant(BitmapSource source)
    {
        try
        {
            var small = new TransformedBitmap(new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0),
                new ScaleTransform(16.0 / Math.Max(1, source.PixelWidth), 16.0 / Math.Max(1, source.PixelHeight)));
            var width = small.PixelWidth;
            var height = small.PixelHeight;
            if (width <= 0 || height <= 0) return Colors.SlateGray;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            small.CopyPixels(pixels, stride, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                double blue = pixels[i], green = pixels[i + 1], red = pixels[i + 2], alpha = pixels[i + 3];
                if (alpha < 40) continue;

                var max = Math.Max(red, Math.Max(green, blue));
                var min = Math.Min(red, Math.Min(green, blue));
                if (max <= 0) continue;
                var saturation = (max - min) / max;
                var weight = (alpha / 255.0) * Math.Pow(saturation, 3) + 0.02;

                r += red * weight;
                g += green * weight;
                b += blue * weight;
                total += weight;
            }
            if (total <= 0) return Colors.SlateGray;

            var colour = Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(b / total));
            return Lift(colour);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or OverflowException)
        {
            return Colors.SlateGray;
        }
    }

    /// <summary>
    /// Pushes a colour to where it reads as a thin line against the wallpaper.
    /// A five-pixel bar of a near-black or near-white icon colour is invisible.
    /// </summary>
    private static Color Lift(Color colour)
    {
        var (h, s, l) = ToHsl(colour);
        s = Math.Clamp(s < 0.18 ? 0.18 : s, 0.18, 0.95);
        l = Math.Clamp(l, StartStyle.IsLight ? 0.32 : 0.48, StartStyle.IsLight ? 0.58 : 0.72);
        return Hearth.Core.Icons.IconAnalysis.FromHsl(h, s, l);
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (Math.Abs(max - min) < 0.0001) return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (Math.Abs(max - r) < 0.0001) h = (g - b) / d + (g < b ? 6 : 0);
        else if (Math.Abs(max - g) < 0.0001) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    /// <summary>A colour for something with no icon: stable across runs, which string.GetHashCode is not.</summary>
    private static Color StableColour(string key)
    {
        uint hash = 2166136261;
        foreach (var ch in key)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return Hearth.Core.Icons.IconAnalysis.FromHsl(hash % 360, 0.55, StartStyle.IsLight ? 0.45 : 0.62);
    }

    // ---- The bar's own things --------------------------------------------

    private readonly List<StackFlyout> _stacks = [];

    /// <summary>The widgets in one section, in order.</summary>
    public IEnumerable<BarWidget> WidgetsIn(BarSection section) =>
        Settings.Widgets.Where(w => w.Section == section);

    /// <summary>
    /// What a stack shows. A folder stack lists its folder; the desktop stack
    /// is every desktop item Hearth already knows about, which is cheaper and
    /// tidier than reading the folder again.
    /// </summary>
    public List<LauncherItem> StackContents(BarWidget widget)
    {
        try
        {
            if (widget.Kind == BarWidgetKind.DesktopStack)
                return Hosting.DesktopHost.Current?.Surface?.VisibleItems().ToList() ?? [];

            if (widget.Kind == BarWidgetKind.RecycleBin)
                return [];

            if (widget.Target.Length == 0 || !System.IO.Directory.Exists(widget.Target)) return [];

            var items = new List<LauncherItem>();
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(widget.Target)
                         .OrderBy(e => e, StringComparer.CurrentCultureIgnoreCase)
                         .Take(64))
            {
                var name = System.IO.Path.GetFileName(entry);
                if (name.StartsWith('.')) continue;
                items.Add(new LauncherItem
                {
                    Id = entry,
                    DisplayName = System.IO.Path.GetFileNameWithoutExtension(name) is { Length: > 0 } stem ? stem : name,
                    Kind = System.IO.Directory.Exists(entry) ? LauncherItemKind.Folder : LauncherItemKind.File,
                    Target = entry,
                });
            }
            return items;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Write($"bar: could not read the stack '{widget.Target}': {ex.Message}");
            return [];
        }
    }

    public void Register(StackFlyout flyout) => _stacks.Add(flyout);

    /// <summary>Whether anything of the bar's is open, so an auto-hiding bar stays put under it.</summary>
    public bool HasOpenFlyout => _stacks.Any(f => f.IsOpen) ||
                                 Hosting.StartMenuController.Current?.IsOpen == true;

    /// <summary>
    /// Closes every open stack except the one whose button was just clicked —
    /// that one is about to toggle itself, and closing it here would leave the
    /// click with nothing to open.
    /// </summary>
    public void CloseStacksExcept(StackButton? keep)
    {
        foreach (var stack in _stacks.ToList())
        {
            if (keep is not null && stack.Owner == keep) continue;
            try { stack.CloseStack(); } catch (Exception ex) { Log.Write($"bar: closing a stack: {ex.Message}"); }
            _stacks.Remove(stack);
        }
    }

    /// <summary>Shuts every open stack, so launching from one does not leave it hanging about.</summary>
    public void CloseStacks()
    {
        foreach (var stack in _stacks.ToList())
        {
            try { stack.CloseStack(); } catch (Exception ex) { Log.Write($"bar: closing a stack: {ex.Message}"); }
        }
        _stacks.Clear();
    }

    /// <summary>The menu on one of the bar's own things.</summary>
    public void ShowWidgetMenu(FrameworkElement anchor, BarWidget widget)
    {
        var menu = StartStyle.NewMenu(anchor);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

        if (widget.Kind is BarWidgetKind.Stack or BarWidgetKind.DesktopStack)
        {
            var how = new MenuItem { Header = "Open as" };
            foreach (var (value, label) in new[]
                     {
                         (StackStyle.Grid, "Grid"), (StackStyle.Fan, "Fan"), (StackStyle.List, "List"),
                     })
            {
                var chosen = value;
                var entry = new MenuItem { Header = label, IsCheckable = true, IsChecked = widget.Stack == value };
                entry.Click += (_, _) => { widget.Stack = chosen; SaveAndApply(); };
                how.Items.Add(entry);
            }
            menu.Items.Add(how);
            if (widget.Kind == BarWidgetKind.Stack)
                menu.Items.Add(Item("Open the folder", () => ShellLauncher.Open(widget.Target)));
        }

        var where = new MenuItem { Header = "Move to" };
        foreach (var (value, label) in new[]
                 {
                     (BarSection.Start, "The start of the bar"),
                     (BarSection.Middle, "Between pinned and open"),
                     (BarSection.End, "The end of the bar"),
                 })
        {
            var chosen = value;
            var entry = new MenuItem { Header = label, IsCheckable = true, IsChecked = widget.Section == value };
            entry.Click += (_, _) => { widget.Section = chosen; SaveAndApply(); };
            where.Items.Add(entry);
        }
        menu.Items.Add(where);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove from the bar", () =>
        {
            Settings.Widgets.RemoveAll(w => w.Id == widget.Id);
            SaveAndApply();
        }));
        menu.Items.Add(Item("Bar settings...", () => Views.SettingsScreen.SettingsWindow.Open("bar")));
        menu.IsOpen = true;
    }

    /// <summary>Adds one of the bar's own things and shows it.</summary>
    public void AddWidget(BarWidgetKind kind, string target = "", BarSection section = BarSection.End)
    {
        Settings.Widgets.Add(new BarWidget { Kind = kind, Target = target, Section = section });
        SaveAndApply();
    }

    /// <summary>The size an applet is laid out at before the bar scales it down.</summary>
    private static readonly Size AppletSize = new(210, 105);

    /// <summary>One widget, shrunk to fit a bar. Null when there is no such widget.</summary>
    public FrameworkElement? BuildApplet(string id, double height)
    {
        try
        {
            var widget = Widgets.WidgetRegistry.All.FirstOrDefault(w => w.Id == id);
            if (widget is null) return null;
            // Laid out at the desktop's own density and scaled down by the
            // Viewbox, so a widget written for a 2x1 tile needs to know
            // nothing about living in a bar.
            var context = new Widgets.WidgetContext
            {
                PixelSize = AppletSize,
                Scale = 1.0,
                DarkTheme = !StartStyle.IsLight,
                Bare = true,
            };
            var built = widget.CreateView(context);
            if (built is null) return null;

            // The size has to be imposed and not merely declared. A Viewbox
            // measures its child at infinity, and a widget that reads its own
            // layout off the width it is given then has nothing to settle on:
            // the Shelf picks a column count from its width, grows wide enough
            // to hold those columns, is measured wider, picks more columns,
            // and so on without bound. One shelf applet on the Panel pegged a
            // core and ran the process to several GB that way.
            built.Width = AppletSize.Width;
            built.Height = AppletSize.Height;

            return new Viewbox
            {
                Child = built,
                Height = height,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
            };
        }
        catch (Exception ex)
        {
            Log.Error($"bar applet '{id}'", ex);
            return null;
        }
    }

    // ---- Actions ---------------------------------------------------------

    /// <summary>The bar's Start button: a second click closes the menu again.</summary>
    public void OpenStart(string? tab = null) => Hosting.StartMenuController.Current?.Toggle(tab);

    public void ShowDesktop() => WindowTools.SendWinChord('D');

    public void LaunchAnother(BarAppGroup group)
    {
        try
        {
            var path = group.ProgramPath.Length > 0 ? group.ProgramPath : group.Program;
            var item = App.Apps.Find(path) ?? new LauncherItem
            {
                Id = path,
                DisplayName = group.Program,
                Kind = LauncherItemKind.File,
                Target = path,
            };
            AppLauncher.Launch(item);
        }
        catch (Exception ex)
        {
            Log.Error($"bar: launching {group.Program}", ex);
        }
    }

    /// <summary>The menu on an app's button: its windows, pin, and close.</summary>
    public void ShowAppMenu(FrameworkElement anchor, BarAppGroup group)
    {
        var menu = StartStyle.NewMenu(anchor);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

        foreach (var window in group.Windows.Take(12))
        {
            var entry = window;
            menu.Items.Add(Item(entry.Title.Length > 0 ? entry.Title : group.Program,
                () => WindowTools.Activate(entry.Handle)));
        }
        if (group.Windows.Count > 0) menu.Items.Add(new Separator());

        menu.Items.Add(Item($"Open {group.Program}", () => LaunchAnother(group)));
        menu.Items.Add(IsPinned(group)
            ? Item("Unpin from the bar", () => Unpin(group))
            : Item("Pin to the bar", () => Pin(group)));

        if (group.Windows.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(group.Windows.Count == 1 ? "Close" : $"Close all {group.Windows.Count}", () =>
            {
                foreach (var window in group.Windows) WindowTools.Close(window.Handle);
            }));
        }
        menu.IsOpen = true;
    }

    /// <summary>The bar's own menu: its style, its settings, and the way out.</summary>
    public void ShowBarMenu(FrameworkElement anchor)
    {
        var menu = StartStyle.NewMenu(anchor);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

        var style = new MenuItem { Header = "Bar style" };
        foreach (var (value, label) in BarStyleNames())
        {
            var chosen = value;
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = Settings.Style == value };
            item.Click += (_, _) =>
            {
                Settings.Style = chosen;
                var (min, max, preferred) = Settings.ThicknessRange();
                if (Settings.Thickness < min || Settings.Thickness > max) Settings.Thickness = preferred;
                SaveAndApply();
            };
            style.Items.Add(item);
        }
        menu.Items.Add(style);

        menu.Items.Add(Item("Bar settings...", () => Views.SettingsScreen.SettingsWindow.Open("bar")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Turn the Hearth bar off", () => SetEnabled(false)));
        menu.IsOpen = true;
    }

    public static IEnumerable<(BarStyle Value, string Label)> BarStyleNames() =>
    [
        (BarStyle.Taskbar, "Taskbar"),
        (BarStyle.Panel, "Panel"),
        (BarStyle.MenuBarAndDock, "Menu bar and dock"),
        (BarStyle.Docklike, "Docklike taskbar"),
        (BarStyle.Halo, "Halo (the screen's edge)"),
    ];

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error($"bar menu '{header}'", ex); }
        };
        return item;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enforce.Stop();
        _rebuild?.Stop();
        Windows.Changed -= OnWindowsChanged;
        Windows.Dispose();
        Tear();
        if (ShellTaskbar.IsHiddenByUs) ShellTaskbar.Restore();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
