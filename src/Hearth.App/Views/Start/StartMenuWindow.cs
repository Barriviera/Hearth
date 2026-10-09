using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Hearth.App.Controls;
using Hearth.App.Hosting;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;
using Hearth.Core.Shell;

namespace Hearth.App.Views.Start;

/// <summary>
/// Hearth's Start menu: a 4:3 panel on a Mica backdrop with a search box and
/// four views — Pages (pinned apps, paged), All apps, Categories and Widgets.
///
/// One instance lives for the whole session and is hidden rather than closed,
/// so opening it is instant; widget views are the exception, built when their
/// tab is shown and dropped when the menu hides so their timers stop.
/// </summary>
internal sealed partial class StartMenuWindow : Window
{
    /// <summary>Design size in DIPs; shrunk to fit small screens, always 4:3.</summary>
    private const double DesignWidth = 880;
    private const double DesignHeight = 660;

    public const string PagesTab = "pages";
    public const string AllAppsTab = "all";
    public const string CategoriesTab = "categories";
    public const string WidgetsTab = "widgets";

    private readonly StartLayout _layout;
    private readonly StartMetrics _metrics;

    /// <summary>The density this window was built for; the controller rebuilds it when the setting changes.</summary>
    public StartDensity Density { get; }

    private readonly Grid _root;
    private readonly TextBox _search;
    private readonly TextBlock _searchHint;
    private readonly Grid _content;
    private readonly Dictionary<string, TabPill> _tabs = [];
    private readonly Dictionary<string, FrameworkElement> _views = [];
    private readonly TranslateTransform _openSlide = new();
    private readonly Grid _backdrop = new();
    private readonly Border _avatar = new();
    private IntPtr _heldTaskbar;
    private FrameworkElement _searchBox = null!;
    private FrameworkElement _tabStrip = null!;

    private Dictionary<string, LauncherItem> _apps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The list _apps was built from, so an unchanged list is not rebuilt.</summary>
    private IReadOnlyList<LauncherItem>? _appsSource;
    private string _activeTab = PagesTab;
    private Grid _foreground = null!;
    private bool _allowClose;
    private bool _hiding;

    /// <summary>Set while a dialog of ours (widget setup) has focus, so losing it does not close the menu.</summary>
    private bool _suppressHide;

    public StartMenuWindow(StartLayout layout)
    {
        _layout = layout;
        Density = StartMetrics.Effective(App.Settings);
        _metrics = StartMetrics.For(Density);

        // The grid is part of the density: Compact's pages hold more. Anything
        // the new grid cannot hold is re-placed, so the change is saved.
        var (columns, rows) = StartMetrics.GridFor(Density);
        if (layout.UseGrid(columns, rows)) SaveLayout();

        Title = "Start — Hearth";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = DesignWidth;
        Height = DesignHeight;
        Foreground = StartStyle.Text;
        FontFamily = StartStyle.Body;
        Resources = StartStyle.WindowResources();

        // The frame is extended over the whole window so the system backdrop
        // shows through; the client area itself paints nothing.
        Background = Brushes.Transparent;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1),
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // ---- search -----------------------------------------------------
        _search = new TextBox
        {
            Background = StartStyle.Panel,
            BorderBrush = StartStyle.CardEdge,
            Foreground = StartStyle.Text,
            CaretBrush = StartStyle.Text,
            SelectionBrush = StartStyle.Accent,
            FontSize = _metrics.SearchFont,
            Padding = _metrics.SearchPadding,
        };
        _search.TextChanged += (_, _) => OnSearchChanged();
        _search.PreviewKeyDown += OnSearchKey;

        _searchHint = StartStyle.Label("Search apps, settings and files", _metrics.SearchFont, StartStyle.Faint);
        _searchHint.IsHitTestVisible = false;
        _searchHint.Margin = new Thickness(_metrics.HintLeft, 0, 0, 0);

        var searchGlyph = StartStyle.GlyphText(StartStyle.Search, _metrics.SearchFont, StartStyle.Muted);
        searchGlyph.HorizontalAlignment = HorizontalAlignment.Left;
        searchGlyph.Margin = new Thickness(_metrics.SearchGlyphLeft, 0, 0, 0);
        searchGlyph.IsHitTestVisible = false;

        var searchBox = new Grid { Margin = _metrics.SearchMargin, Children = { _search, searchGlyph, _searchHint } };
        _searchBox = searchBox;

        // ---- tabs -------------------------------------------------------
        var tabStrip = new StackPanel { Orientation = Orientation.Horizontal, Margin = _metrics.TabsMargin };
        _tabStrip = tabStrip;
        foreach (var (key, text) in new[]
                 {
                     (PagesTab, "Pages"), (AllAppsTab, "All apps"),
                     (CategoriesTab, "Categories"), (WidgetsTab, "Widgets"),
                 })
        {
            var pill = new TabPill(key, text, _metrics);
            pill.Clicked += _ => ShowTab(key);
            _tabs[key] = pill;
            tabStrip.Children.Add(pill);
        }

        // ---- content ----------------------------------------------------
        _content = new Grid { Margin = _metrics.ContentMargin, ClipToBounds = true };
        _views[PagesTab] = BuildPagesView();
        _views[AllAppsTab] = BuildAllAppsView();
        _views[CategoriesTab] = BuildCategoriesView();
        _views[WidgetsTab] = BuildWidgetsView();
        _views["search"] = BuildSearchView();
        foreach (var view in _views.Values)
        {
            view.Visibility = Visibility.Collapsed;
            _content.Children.Add(view);
        }

        // ---- footer -----------------------------------------------------
        var footer = BuildFooter();

        _root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
            },
        };
        // The backdrop fills the window and stays put; everything else slides
        // in over it when the menu opens.
        var foreground = new Grid { RenderTransform = _openSlide, RowDefinitions = { } };
        foreach (var row in _root.RowDefinitions.ToList())
        {
            _root.RowDefinitions.Remove(row);
            foreground.RowDefinitions.Add(row);
        }
        Grid.SetRow(searchBox, 0);
        Grid.SetRow(tabStrip, 1);
        Grid.SetRow(_content, 2);
        Grid.SetRow(footer, 3);
        foreground.Children.Add(searchBox);
        foreground.Children.Add(tabStrip);
        foreground.Children.Add(_content);
        foreground.Children.Add(footer);
        _root.Children.Add(_backdrop);
        _root.Children.Add(foreground);
        _foreground = foreground;
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(OnHorizontalScroll);
            // Out of Alt+Tab, like the real Start menu.
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
            ApplyBackdropSetting();
        };

        Deactivated += (_, _) => HideMenuFor("deactivated", "");
        // Full screen: a tap on the backdrop around the panel closes Start.
        MouseLeftButtonUp += (_, e) =>
        {
            if (_fullScreen && e.OriginalSource is DependencyObject source && !IsWithin(source, _foreground))
                HideMenuFor("tapped outside", "");
        };
        PreviewKeyDown += OnWindowKey;
        PreviewTextInput += OnWindowText;
        App.Badges.Changed += () => Dispatcher.InvokeAsync(RefreshBadges);
        App.Apps.Changed += () => { if (IsVisible) RefreshApps(); };

        // The lists work out their own column count from the room they have,
        // so a change of room (full screen, a different placement or scale)
        // has to rebuild them.
        _content.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) < 1) return;
            OnContentWidthChanged();
        };

        ShowTab(PagesTab);
    }

    // ---- Showing and hiding ---------------------------------------------

    /// <summary>Opens on the display at <paramref name="screenPoint"/>, just above its taskbar.</summary>
    public async void ShowMenu(Point screenPoint)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var marks = new List<string>();
        void Mark(string what) => marks.Add($"{what} {clock.ElapsedMilliseconds}");

        _hiding = false;
        _search.Text = string.Empty;
        _searchHint.Visibility = Visibility.Visible;
        ResetPages();
        Mark("pages");
        if (_openInEditMode)
        {
            _openInEditMode = false;
            SetEditing(true);
        }
        CloseCategory();
        _allScroll.ScrollToTop();
        _categoryCards.ScrollToTop();
        ShowTab(_activeTab);
        Mark("tabs");

        // Placed before it is shown, in physical pixels: WPF's own Left/Top are
        // DIPs of whichever display the window was last on, and positioning
        // after Show would flash the menu at its old spot.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var placement = PlaceOn(screenPoint);
        if (!IsVisible && placement is { } rect && App.Settings.StartBackdrop == StartBackdrop.Blur)
            CaptureBackdrop(rect);
        Mark("backdrop");

        // Full screen (tablet mode) has no taskbar to hold up.
        HoldTaskbar(hwnd, _fullScreen || Tablet.TaskbarControl.IsHiddenByUs ? IntPtr.Zero : placement?.Taskbar ?? IntPtr.Zero);

        var wasVisible = IsVisible;
        if (!wasVisible) await PresentAsync(hwnd, placement).ConfigureAwait(true);
        Mark("show");
        StartTrigger.ClaimForegroundRight();
        Activate();
        SetForegroundWindow(hwnd);
        Mark("focus");

        // With the menu owned by an auto-hidden taskbar, giving the taskbar
        // focus for an instant brings it up, and ownership keeps it up until
        // the menu goes (measured: it stayed up throughout; without ownership
        // it slid away within a second). The hand-off waits on Explorer
        // (about 250 ms in real use), so it runs once the menu has drawn.
        if (_heldTaskbar != IntPtr.Zero)
        {
            _suppressHide = true;
            _ = Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (IsVisible && _heldTaskbar != IntPtr.Zero)
                    {
                        SetForegroundWindow(_heldTaskbar);
                        StartTrigger.ClaimForegroundRight();
                        SetForegroundWindow(hwnd);
                        _search.Focus();
                    }
                }
                finally
                {
                    // Deactivation from the hand-off arrives as queued
                    // messages; release the guard only after they are handled.
                    _ = Dispatcher.BeginInvoke(() => _suppressHide = false, System.Windows.Threading.DispatcherPriority.Background);
                }
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        _search.Focus();
        if (wasVisible) AnimateOpen();
        LoadAccountPicture();
        Mark("done");
        Log.Write($"start menu: shown, taskbar held={_heldTaskbar != IntPtr.Zero}, active={IsActive}; ms: {string.Join(", ", marks)}");
        _ = Dispatcher.BeginInvoke(() => Log.Write($"start menu: first frame idle at {clock.ElapsedMilliseconds} ms"),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // Compared by identity, not by count: _apps is keyed by id, so two
        // entries sharing one made the counts differ for ever and rebuilt every
        // view on every open. A list that really changed arrives through
        // InstalledApps.Changed instead.
        var apps = await App.Apps.GetAsync().ConfigureAwait(true);
        if (!ReferenceEquals(apps, _appsSource)) RefreshApps(apps);
        if (_activeTab == WidgetsTab) BuildWidgets();
    }

    /// <summary>
    /// Builds everything and lays it out once, off screen, so the first real
    /// open is as quick as later ones. Measured: a cold first open took about
    /// 670 ms to settle, a warm one 140 ms.
    /// </summary>
    public void Prewarm(IReadOnlyList<LauncherItem> apps)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        RefreshApps(apps);
        LoadAccountPicture();

        ShowActivated = false;
        SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, (int)DesignWidth, (int)DesignHeight, SWP_NOZORDER | SWP_NOACTIVATE);
        Show();
        foreach (var view in _views.Values) view.Visibility = Visibility.Visible;
        _root.UpdateLayout();
        foreach (var (key, view) in _views) view.Visibility = key == _activeTab ? Visibility.Visible : Visibility.Collapsed;
        _root.UpdateLayout();
        Hide();
        ShowActivated = true;

        // The backdrop's own path is warmed too: the first capture pays for
        // GDI's screen DC and for WPF's software render target, which together
        // cost about 100 ms and would otherwise land on the first real open.
        // A one-pixel area is enough to walk all of it.
        _ = StartBackdropCapture.Build(
            StartBackdropCapture.Capture(new Int32Rect(0, 0, 8, 8), 4) ??
            System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4),
            0, System.Windows.Media.Brushes.Transparent);
    }

    public void HideMenu([System.Runtime.CompilerServices.CallerMemberName] string caller = "") => HideMenuFor(null, caller);

    private void HideMenuFor(string? reason, string caller)
    {
        if (_hiding || _suppressHide || !IsVisible) return;
        Log.Write($"start menu: hide ({reason ?? caller})");
        _hiding = true;
        ClearWidgets();
        ClearPages();
        CloseLetterJump();

        // Drop ownership first, or Windows hands focus to the taskbar as the
        // menu hides and the taskbar stays up.
        ReleaseTaskbar();
        Hide();
    }

    /// <summary>
    /// Called for every mouse press anywhere while the menu is open. Hearth's
    /// desktop never takes focus, so clicking it does not deactivate the menu;
    /// a press outside the menu (and outside Hearth's own popups) closes it.
    /// </summary>
    public void OnMousePressedAt(int x, int y)
    {
        if (!IsVisible || _suppressHide) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (GetWindowRect(hwnd, out var rect) && x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom) return;

        // Context menus and tooltips are separate windows of this process.
        var under = WindowFromPoint(new POINT { X = x, Y = y });
        GetWindowThreadProcessId(under, out var owner);
        var ours = owner == Environment.ProcessId && under != DesktopHost.Current?.Handle;
        if (ours) return;

        HideMenuFor($"pressed outside at {x},{y}", "");
    }

    /// <summary>Whether this tab is the one on show, so a Start button knows a second click means close.</summary>
    public bool IsShowing(string tab) => _activeTab == tab;

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideMenu();
        }
        base.OnClosing(e);
    }

    private readonly record struct Placement(Int32Rect Rect, double Scale, IntPtr Taskbar, bool FullScreen = false);

    private bool _fullScreen;

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (var current = node; current is not null;
             current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    /// <summary>
    /// Tablet mode's Start: the whole work area of the display (which already
    /// leaves out the navigation bar). The content fills it at a slightly
    /// larger scale than the floating menu, and the Pages view uses the room
    /// to show pages side by side (see StartMenuWindow.Spread.cs) rather than
    /// one enlarged page.
    /// </summary>
    private Placement? PlaceFullScreen(IntPtr hwnd, IntPtr monitor, MONITORINFO info, double scale)
    {
        var work = info.rcWork;
        var width = work.Right - work.Left;
        var height = work.Bottom - work.Top;
        var widthDips = width / scale;
        var heightDips = height / scale;
        var ui = Math.Clamp(Math.Min(widthDips / 1440, heightDips / 900), 1, 1.4);

        _root.LayoutTransform = Transform.Identity;
        _foreground.Width = widthDips / ui;
        _foreground.Height = heightDips / ui;
        _foreground.LayoutTransform = new ScaleTransform(ui, ui);
        if (StartStyle.SetRenderScale(ui)) RerenderIcons();
        _foreground.HorizontalAlignment = HorizontalAlignment.Center;
        _foreground.VerticalAlignment = VerticalAlignment.Center;
        _searchBox.MaxWidth = 760;
        _searchBox.HorizontalAlignment = HorizontalAlignment.Center;
        _searchBox.Width = 760;
        _tabStrip.HorizontalAlignment = HorizontalAlignment.Center;
        SetCorners(hwnd, round: false);

        SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        return new Placement(new Int32Rect(work.Left, work.Top, width, height), scale, IntPtr.Zero, FullScreen: true);
    }

    /// <summary>
    /// Rebuilds every tile so its icon is fetched again at the new scale.
    /// Queued rather than run here, because this is called from the middle of
    /// placing the window, before it has been shown.
    /// </summary>
    private void RerenderIcons() => Dispatcher.BeginInvoke(() =>
    {
        if (_apps.Count == 0) return;
        RebuildPages();
        RebuildAllApps();
        RebuildCategories();
    }, System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>Pages were built for the other presentation; lay out and build them again.</summary>
    private void RelayoutPagesNow()
    {
        _root.UpdateLayout();
        RebuildPages();
    }

    private void ResetFloatingLayout(IntPtr hwnd)
    {
        _foreground.Width = double.NaN;
        _foreground.Height = double.NaN;
        _foreground.LayoutTransform = Transform.Identity;
        _foreground.HorizontalAlignment = HorizontalAlignment.Stretch;
        _foreground.VerticalAlignment = VerticalAlignment.Stretch;
        _searchBox.MaxWidth = double.PositiveInfinity;
        _searchBox.Width = double.NaN;
        _searchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        _tabStrip.HorizontalAlignment = HorizontalAlignment.Left;
        _content.MaxWidth = double.PositiveInfinity;
        SetCorners(hwnd, round: true);
    }

    private static void SetCorners(IntPtr hwnd, bool round)
    {
        var preference = round ? 2 : 1; // DWMWCP_ROUND : DWMWCP_DONOTROUND
        DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
    }

    /// <summary>
    /// Centres the menu above the taskbar of the display at the point, a
    /// Windows-sized gap clear of it. An auto-hidden taskbar reserves no space
    /// in the work area, so its shown height is measured from the window.
    /// </summary>
    private Placement? PlaceOn(Point screenPoint)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromPoint(new POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;

        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        var fullScreen = Tablet.TabletMode.Current?.FullScreenStart == true;
        var modeChanged = fullScreen != _fullScreen;
        if (modeChanged && !fullScreen) ResetFloatingLayout(hwnd);
        _fullScreen = fullScreen;
        if (fullScreen)
        {
            var placed = PlaceFullScreen(hwnd, monitor, info, scale);
            _content.MaxWidth = _activeTab != PagesTab ? 1100 : double.PositiveInfinity;
            if (modeChanged) RelayoutPagesNow();
            return placed;
        }
        if (modeChanged) Dispatcher.BeginInvoke(RelayoutPagesNow);

        var screen = info.rcMonitor;
        var work = info.rcWork;
        var margin = (int)Math.Round(Math.Clamp(App.Settings.StartMargin, 0, 200) * scale);
        var wanted = Math.Clamp(App.Settings.StartScale, 0.6, 2.0);
        var placement = ResolvePlacement(monitor);

        if (placement != StartPlacement.AboveTaskbar)
            return PlaceDetached(hwnd, work, scale, wanted, margin, placement);

        // Hearth's own bar comes first. It is a registered app bar, so the work
        // area already excludes its strip; all this decides is which end of
        // that area the menu stands on. The Windows taskbar is not consulted
        // at all in that case — with the bar running it is usually the one
        // Hearth has taken off the screen, and standing on a bar nobody can
        // see put the menu against the wrong edge entirely.
        var service = Bar.BarService.Current is { IsRunning: true } running ? running : null;
        var hearthEdge = service?.BarEdgeFor(monitor);
        var shellHidden = Bar.ShellTaskbar.IsHiddenByUs || Tablet.TaskbarControl.IsHiddenByUs;

        var taskbar = IntPtr.Zero;
        var barHeight = 0;
        var barAtTop = false;
        var barVertical = false;

        if (hearthEdge is { } edge)
        {
            barAtTop = edge == ScreenEdge.Top;
            barVertical = edge is ScreenEdge.Left or ScreenEdge.Right;
        }
        else if (!shellHidden)
        {
            taskbar = TaskbarOn(monitor);
            if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out var bar))
            {
                barHeight = bar.Bottom - bar.Top;
                barAtTop = bar.Top <= screen.Top + 1 && bar.Bottom < screen.Bottom - barHeight;
            }
        }

        var top = barAtTop ? Math.Max(work.Top, screen.Top + barHeight) : work.Top;
        var bottom = barAtTop ? work.Bottom : Math.Min(work.Bottom, screen.Bottom - barHeight);
        var areaWidth = work.Right - work.Left;
        var areaHeight = bottom - top;

        // 4:3, at design size if it fits, with room for the gaps.
        var fit = Fit(wanted, areaWidth * 0.92, areaHeight - margin * 2, scale);
        var width = (int)Math.Round(DesignWidth * scale * fit);
        var height = (int)Math.Round(DesignHeight * scale * fit);

        // Lined up with the Start button rather than always centred.
        //
        // Windows 11 centres its taskbar by default and can be set to align it
        // left; Windows 10 was always left; Hearth's own bars put Start at the
        // left (Taskbar, Panel), in the middle of a centred cluster (Docklike)
        // or at a corner (the ring). A menu that always came out of the middle
        // of the screen was right only by accident.
        var anchor = service?.StartButtonRect(monitor) ?? StartTrigger.StartButtonRect(monitor);
        int x, y;

        if (barVertical)
        {
            // A bar down one side: the menu sits beside it, level with the button.
            x = hearthEdge == ScreenEdge.Left ? work.Left + margin : work.Right - width - margin;
            y = anchor is { } side
                ? (int)Math.Round(side.Top + side.Height / 2) - height / 2
                : top + (areaHeight - height) / 2;
        }
        else
        {
            x = anchor is { } button
                ? (int)Math.Round(button.Left + button.Width / 2) - width / 2
                : work.Left + (areaWidth - width) / 2;
            y = barAtTop ? top + margin : bottom - height - margin;
        }

        // Whatever the anchor says, it has to stay on this display.
        x = Math.Clamp(x, work.Left + margin, Math.Max(work.Left + margin, work.Right - width - margin));
        y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - height - margin));

        // Content is laid out at design size and scaled as a whole, so the grid
        // never reflows into a different shape on a small screen.
        _root.LayoutTransform = Scale(fit);
        if (StartStyle.SetRenderScale(fit)) RerenderIcons();
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        return new Placement(new Int32Rect(x, y, width, height), scale * fit, taskbar);
    }

    /// <summary>
    /// The detached placements: a corner, an edge or the middle of the work
    /// area, with no relationship to the taskbar at all.
    ///
    /// The work area is used rather than the whole screen so the menu still
    /// keeps clear of whatever bars are docked — the real taskbar, Hearth's own
    /// bar, anyone else's app bar — without being anchored to any of them. No
    /// taskbar is held up either: a detached menu is not standing on one, so an
    /// auto-hidden bar is left to slide away as it normally would.
    /// </summary>
    private Placement PlaceDetached(IntPtr hwnd, RECT work, double scale, double wanted, int margin,
        StartPlacement placement)
    {
        if (placement == StartPlacement.CentreLarge) wanted *= 1.25;

        var areaWidth = work.Right - work.Left;
        var areaHeight = work.Bottom - work.Top;

        var fit = Fit(wanted, areaWidth - margin * 2, areaHeight - margin * 2, scale);
        var width = (int)Math.Round(DesignWidth * scale * fit);
        var height = (int)Math.Round(DesignHeight * scale * fit);

        var x = placement switch
        {
            StartPlacement.BottomLeft or StartPlacement.TopLeft or StartPlacement.LeftCentre => work.Left + margin,
            StartPlacement.BottomRight or StartPlacement.TopRight or StartPlacement.RightCentre => work.Right - width - margin,
            _ => work.Left + (areaWidth - width) / 2,
        };
        var y = placement switch
        {
            StartPlacement.TopLeft or StartPlacement.TopCentre or StartPlacement.TopRight => work.Top + margin,
            StartPlacement.BottomLeft or StartPlacement.BottomCentre or StartPlacement.BottomRight => work.Bottom - height - margin,
            _ => work.Top + (areaHeight - height) / 2,
        };

        _root.LayoutTransform = Scale(fit);
        if (StartStyle.SetRenderScale(fit)) RerenderIcons();
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        return new Placement(new Int32Rect(x, y, width, height), scale * fit, IntPtr.Zero);
    }

    /// <summary>The size multiplier, cut down to whatever room there is.</summary>
    private static double Fit(double wanted, double availableWidth, double availableHeight, double scale) =>
        Math.Max(0.4, Math.Min(wanted, Math.Min(
            availableWidth / (DesignWidth * scale),
            availableHeight / (DesignHeight * scale))));

    private static Transform Scale(double fit) =>
        Math.Abs(fit - 1) < 0.001 ? Transform.Identity : new ScaleTransform(fit, fit);

    /// <summary>
    /// Turns Auto into a real placement.
    ///
    /// Almost always <see cref="StartPlacement.AboveTaskbar"/>, because that
    /// branch is the one that knows about bars: it finds whichever bar is on
    /// this display — Hearth's own first, the Windows taskbar otherwise — reads
    /// its edge, and lines the menu up with the Start button on it. An earlier
    /// version mapped the edge onto the centred detached placements instead,
    /// which put the menu in the middle of the screen whatever the bar looked
    /// like, and that is what the user saw.
    ///
    /// The ring is the exception: it has no edge to come from, so the middle is
    /// the honest answer.
    /// </summary>
    private static StartPlacement ResolvePlacement(IntPtr monitor)
    {
        var placement = App.Settings.StartPlacement;
        if (placement != StartPlacement.Auto) return placement;
        if (Bar.BarService.Current is { IsRunning: true, Settings.Style: BarStyle.Halo }) return StartPlacement.Centre;
        return StartPlacement.AboveTaskbar;
    }

    /// <summary>The taskbar window on a display: the primary one, or a secondary one.</summary>
    private static IntPtr TaskbarOn(IntPtr monitor)
    {
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero && MonitorFromWindow(primary, MONITOR_DEFAULTTONEAREST) == monitor) return primary;

        for (var bar = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);
             bar != IntPtr.Zero;
             bar = FindWindowEx(IntPtr.Zero, bar, "Shell_SecondaryTrayWnd", null))
        {
            if (MonitorFromWindow(bar, MONITOR_DEFAULTTONEAREST) == monitor) return bar;
        }
        return primary;
    }

    // ---- Taskbar --------------------------------------------------------

    private static bool TaskbarAutoHides()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return ((long)SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0;
    }

    /// <summary>
    /// Makes the menu an owned window of the taskbar, which is what keeps an
    /// auto-hidden taskbar up while the menu is open, as with Windows' Start.
    /// </summary>
    private void HoldTaskbar(IntPtr hwnd, IntPtr taskbar)
    {
        ReleaseTaskbar();
        if (taskbar == IntPtr.Zero || !TaskbarAutoHides()) return;
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, taskbar);
        _heldTaskbar = taskbar;
    }

    private void ReleaseTaskbar()
    {
        if (_heldTaskbar == IntPtr.Zero) return;
        SetWindowLongPtr(new WindowInteropHelper(this).Handle, GWLP_HWNDPARENT, IntPtr.Zero);
        _heldTaskbar = IntPtr.Zero;
    }

    // ---- Backdrop -------------------------------------------------------

    private void ApplyBackdropSetting()
    {
        var kind = App.Settings.StartBackdrop;
        var applied = DialogChrome.ApplyBackdrop(this, kind, dark: !StartStyle.IsLight);
        _backdrop.Children.Clear();
        _root.Background = kind != StartBackdrop.Blur && !applied ? StartStyle.Scrim : null;
    }

    private void CaptureBackdrop(Placement placement)
    {
        _backdrop.Children.Clear();
        // Full screen has nothing beyond its edges worth sampling.
        var marginDips = placement.FullScreen ? 0 : 60;
        var marginPixels = (int)Math.Round(marginDips * placement.Scale);
        var capture = StartBackdropCapture.Capture(placement.Rect, marginPixels);
        if (capture is null)
        {
            _backdrop.Children.Add(new System.Windows.Shapes.Rectangle { Fill = StartStyle.Scrim });
            return;
        }

        // The capture is in pixels of the display; the window lays out at
        // design size, so the margin is given in the same DIPs.
        _backdrop.Children.Add(StartBackdropCapture.Build(capture, marginDips, StartStyle.Tint));
    }

    // ---- Account picture --------------------------------------------------

    private bool _pictureLoaded;

    /// <summary>
    /// The Windows account picture, from the copies Windows keeps in
    /// C:\Users\Public\AccountPictures\{SID}. The initial stays if there is none.
    /// </summary>
    private void LoadAccountPicture()
    {
        if (_pictureLoaded) return;
        _pictureLoaded = true;
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (sid is null) return;
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) is { Length: > 0 } docs
                    ? System.IO.Path.GetDirectoryName(docs)!
                    : "C:" + System.IO.Path.DirectorySeparatorChar + "Users" + System.IO.Path.DirectorySeparatorChar + "Public",
                "AccountPictures", sid);
            if (!System.IO.Directory.Exists(folder)) return;

            var file = new System.IO.DirectoryInfo(folder).GetFiles("*-Image192.*")
                .Concat(new System.IO.DirectoryInfo(folder).GetFiles("*-Image208.*"))
                .Concat(new System.IO.DirectoryInfo(folder).GetFiles("*.jpg"))
                .Concat(new System.IO.DirectoryInfo(folder).GetFiles("*.png"))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (file is null) return;

            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file.FullName);
            image.DecodePixelWidth = 96;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            _avatar.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            _avatar.Child = null;
        }
        catch (Exception ex)
        {
            Log.Error("account picture", ex);
        }
    }

    /// <summary>
    /// Brings the window up already drawn.
    ///
    /// Shown straight into place, the first thing on screen is DWM's own glass
    /// frame around an empty window: the user saw "an outline, and then it
    /// pops up". WPF composes asynchronously, so the window exists a frame or
    /// two before its content does. Showing it off screen, letting one frame
    /// compose, and only then moving it into place costs about 16 ms and the
    /// menu arrives whole.
    ///
    /// The open animation starts before the move, so the fade is already under
    /// way by the time anything is visible rather than beginning from nothing.
    /// </summary>
    private async Task PresentAsync(IntPtr hwnd, Placement? placement)
    {
        if (placement is not { } target)
        {
            Show();
            AnimateOpen();
            return;
        }

        var rect = target.Rect;
        ShowActivated = false;
        SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        Show();
        ShowActivated = true;

        AnimateOpen();
        _root.UpdateLayout();
        await ComposedAsync().ConfigureAwait(true);

        SetWindowPos(hwnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Completes once a frame has been composed. Rendering fires just before each one, so the second tick means the first is done.</summary>
    private static Task ComposedAsync()
    {
        var done = new TaskCompletionSource();
        var ticks = 0;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (++ticks < 2) return;
            CompositionTarget.Rendering -= handler;
            done.TrySetResult();
        };
        CompositionTarget.Rendering += handler;

        // A window that never renders (the display asleep, the session locked)
        // must not leave the menu waiting for ever.
        _ = Task.Delay(120).ContinueWith(_ => done.TrySetResult(), TaskScheduler.Default);
        return done.Task;
    }

    private void AnimateOpen()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _openSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        // From 0.35 rather than 0: the window is presented mid-animation, and
        // a first frame at zero opacity is the blank outline all over again.
        _foreground.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease });
    }

    // ---- Data -------------------------------------------------------------

    private void RefreshApps() => RefreshApps(App.Apps.Current);

    private void RefreshApps(IReadOnlyList<LauncherItem> apps)
    {
        _appsSource = apps;
        _apps = apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        SeedPagesIfNeeded();
        RebuildPages();
        RebuildAllApps();
        RebuildCategories();
        if (_search.Text.Length > 0) OnSearchChanged();
    }

    private void RefreshBadges()
    {
        foreach (var tile in Descendants<StartTile>(_root)) tile.Tile.Badge = BadgeForTile(tile.Item);
    }

    private void SaveLayout()
    {
        try { _layout.Save(); }
        catch (Exception ex) { Log.Error("start layout save", ex); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    // ---- Tabs ---------------------------------------------------------------

    private void ShowTab(string key)
    {
        if (!_tabs.ContainsKey(key)) return;
        var leavingWidgets = _activeTab == WidgetsTab && key != WidgetsTab;
        _activeTab = key;
        foreach (var (tabKey, pill) in _tabs) pill.IsActive = tabKey == key;

        if (_search.Text.Length > 0) _search.Text = string.Empty;
        foreach (var (viewKey, view) in _views) view.Visibility = viewKey == key ? Visibility.Visible : Visibility.Collapsed;
        _content.MaxWidth = _fullScreen && key != PagesTab ? 1100 : double.PositiveInfinity;

        if (key != CategoriesTab) CloseCategory();
        if (leavingWidgets) ClearWidgets();
        if (key == WidgetsTab && IsVisible) BuildWidgets();
    }

    /// <summary>Opens on a given tab next time ("all", "pages", "categories", "widgets").</summary>
    public void SelectTab(string key)
    {
        if (key == "edit")
        {
            _activeTab = PagesTab;
            _openInEditMode = true;
            return;
        }
        if (_tabs.ContainsKey(key)) _activeTab = key;
    }

    private bool _openInEditMode;

    private void CycleTab(int step)
    {
        var keys = _tabs.Keys.ToList();
        var index = (keys.IndexOf(_activeTab) + step + keys.Count) % keys.Count;
        ShowTab(keys[index]);
    }

    // ---- Keyboard -------------------------------------------------------

    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape:
                if (_letterJump is { Visibility: Visibility.Visible }) CloseLetterJump();
                else if (CloseAppPicker()) { }
                else if (ClosePageFolder()) { }
                else if (_editing && _search.Text.Length == 0) SetEditing(false);
                else if (_search.Text.Length > 0) _search.Text = string.Empty;
                else if (_openCategory is not null) CloseCategory();
                else HideMenu();
                e.Handled = true;
                break;

            case Key.Tab when ctrl:
                CycleTab(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                break;

            case Key.PageDown or Key.PageUp when _activeTab == PagesTab && _search.Text.Length == 0:
                ChangePage(e.Key == Key.PageDown ? 1 : -1);
                e.Handled = true;
                break;

            case Key.Left or Key.Right when _activeTab == PagesTab && _search.Text.Length == 0 &&
                                            (Keyboard.FocusedElement is not TextBox || ReferenceEquals(Keyboard.FocusedElement, _search)):
                ChangePage(e.Key == Key.Right ? 1 : -1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Typing anywhere goes to the search box.</summary>
    private void OnWindowText(object sender, TextCompositionEventArgs e)
    {
        // Other text boxes (a folder name, the app picker) keep their typing.
        if (Keyboard.FocusedElement is TextBox || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        _search.Focus();
        _search.Text += e.Text;
        _search.CaretIndex = _search.Text.Length;
        e.Handled = true;
    }

    // ---- Launching and item menus --------------------------------------

    private void Launch(LauncherItem item)
    {
        HideMenu();
        if (!Services.AppLauncher.Launch(item)) Log.Write($"start: failed to launch '{item.DisplayName}'");
    }

    private void OpenTarget(string target)
    {
        HideMenu();
        if (!ShellLauncher.Open(target)) Log.Write($"start: failed to open '{target}'");
    }

    /// <summary>The right-click menu for any app or file shown in Start.</summary>
    private void ShowItemMenu(LauncherItem item, FrameworkElement anchor, Hearth.Core.Layout.GridGroup? inFolder = null)
    {
        var menu = StartStyle.NewMenu(anchor);

        if (item.Kind == LauncherItemKind.App && JumpListMenu.Add(menu, item.Id, () => HideMenu("jump list")))
            menu.Items.Add(new Separator());

        menu.Items.Add(Item("Open", () => Launch(item)));
        if (item.Kind is LauncherItemKind.App or LauncherItemKind.Shortcut)
            menu.Items.Add(Item("Run as administrator", () =>
            {
                HideMenu();
                ShellLauncher.RunAsAdministrator(item);
            }));
        if (item.FileSystemPath is not null)
            menu.Items.Add(Item("Open file location", () =>
            {
                HideMenu();
                ShellLauncher.OpenFileLocation(item);
            }));

        menu.Items.Add(new Separator());

        AddStartEntries(menu, item, inFolder);

        if (DesktopHost.Current?.Surface is { } desktop && item.Kind == LauncherItemKind.App)
        {
            var onHome = desktop.IsAppOnHome(item);
            menu.Items.Add(Item(onHome ? "Remove from desktop" : "Add to desktop", () => desktop.SetAppOnHome(item, !onHome)));
        }

        if (item.Kind == LauncherItemKind.App)
        {
            var current = CategoryOf(item);
            var move = new MenuItem { Header = "Category" };
            foreach (var category in AppCategorizer.All)
            {
                var entry = new MenuItem { Header = category, IsCheckable = true, IsChecked = category == current };
                entry.Click += (_, _) => SetCategory(item, category);
                move.Items.Add(entry);
            }
            menu.Items.Add(move);

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Uninstall...", () =>
            {
                HideMenu();
                ShellLauncher.OpenInstalledApps();
            }));
        }

        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ---- Footer ---------------------------------------------------------

    private FrameworkElement BuildFooter()
    {
        var name = UserInfo.DisplayName;
        var avatar = _avatar;
        avatar.Width = _metrics.Avatar;
        avatar.Height = _metrics.Avatar;
        avatar.CornerRadius = new CornerRadius(_metrics.Avatar / 2);
        avatar.Background = StartStyle.Accent;
        avatar.Margin = new Thickness(0, 0, 10, 0);
        avatar.Child = new TextBlock
        {
            Text = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?",
            Foreground = StartStyle.OnAccent,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var user = new PressableBorder(radius: 6)
        {
            Padding = _metrics.UserPadding,
            ToolTip = "Account settings",
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { avatar, StartStyle.Label(name, _metrics.UserFont) } },
        };
        user.Clicked += _ => OpenTarget("ms-settings:yourinfo");

        var explorer = new ChromeButton(StartStyle.Folder, tooltip: "File Explorer", glyphSize: _metrics.FooterGlyph);
        explorer.Clicked += _ => OpenTarget("explorer.exe");

        var settings = new ChromeButton(StartStyle.Settings, tooltip: "Settings", glyphSize: _metrics.FooterGlyph);
        settings.Clicked += _ => OpenTarget("ms-settings:");

        var options = new ChromeButton(StartStyle.More, tooltip: "Start menu options", glyphSize: _metrics.FooterGlyph);
        options.Clicked += b => ShowOptionsMenu(b);

        var power = new ChromeButton(StartStyle.Power, tooltip: "Power", glyphSize: _metrics.FooterGlyph);
        power.Clicked += b => ShowPowerMenu(b);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { explorer, settings, options, power },
        };

        var dock = new DockPanel { Margin = _metrics.FooterMargin, LastChildFill = false };
        DockPanel.SetDock(user, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(user);
        dock.Children.Add(right);

        return new Border
        {
            Background = StartStyle.Footer,
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = dock,
        };
    }

    private void ShowPowerMenu(FrameworkElement anchor)
    {
        var menu = StartStyle.NewMenu(anchor);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.Items.Add(Item("Lock", () => { HideMenu(); PowerActions.Lock(); }));
        menu.Items.Add(Item("Sleep", () => { HideMenu(); PowerActions.Sleep(); }));
        menu.Items.Add(Item("Sign out", () => { HideMenu(); PowerActions.SignOut(); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Restart", () => { HideMenu(); PowerActions.Restart(); }));
        menu.Items.Add(Item("Shut down", () => { HideMenu(); PowerActions.ShutDown(); }));
        menu.IsOpen = true;
    }

    private void ShowOptionsMenu(FrameworkElement anchor)
    {
        var settings = App.Settings;
        var menu = StartStyle.NewMenu(anchor);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;

        var backdrop = new MenuItem { Header = "Background" };
        foreach (var (kind, label) in new[]
                 {
                     (StartBackdrop.Blur, "Blur what's behind"),
                     (StartBackdrop.Mica, "Mica (wallpaper only)"),
                     (StartBackdrop.MicaAlt, "Mica Alt"),
                     (StartBackdrop.Acrylic, "Acrylic (needs transparency effects on)"),
                 })
        {
            var entry = new MenuItem { Header = label, IsCheckable = true, IsChecked = settings.StartBackdrop == kind };
            entry.Click += (_, _) =>
            {
                settings.StartBackdrop = kind;
                settings.Save();
                ApplyBackdropSetting();
                if (kind == StartBackdrop.Blur) HideMenu(); // the snapshot is taken as the menu opens
            };
            backdrop.Items.Add(entry);
        }
        menu.Items.Add(backdrop);

        var replace = new MenuItem { Header = "Open with the Windows key and Start button", IsCheckable = true, IsChecked = settings.ReplaceStartMenu };
        replace.Click += (_, _) => StartMenuController.Current?.SetReplaceStart(!settings.ReplaceStartMenu);
        menu.Items.Add(replace);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Hearth settings...", () =>
        {
            HideMenu();
            SettingsScreen.SettingsWindow.Open();
        }));
        if (Tablet.TabletMode.Current is { } tablet)
        {
            menu.Items.Add(Item(tablet.IsActive ? "Leave tablet mode" : "Enter tablet mode", () =>
            {
                HideMenu();
                tablet.Toggle();
            }));
        }
        menu.Items.Add(Item("Open Windows Start", () =>
        {
            HideMenu();
            StartMenuController.Current?.OpenWindowsStart();
        }));

        menu.IsOpen = true;
    }

    // ---- Native ---------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
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

    private const uint ABM_GETSTATE = 4;
    private const long ABS_AUTOHIDE = 1;
    private const int GWLP_HWNDPARENT = -8;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
}
