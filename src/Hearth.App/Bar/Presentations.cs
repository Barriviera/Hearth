using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Hearth.App.Views.Start;
using Hearth.Core.Settings;

namespace Hearth.App.Bar;

/// <summary>
/// Fills a <see cref="BarWindow"/> for one style. Each style is a different
/// idea about what a bar is for, so each one builds its own content; what they
/// share is the window, the window list and the parts in BarParts.
/// </summary>
internal abstract class BarPresentation
{
    protected BarService Service { get; }
    protected BarSettings Settings => Service.Settings;
    protected BarWindow Window { get; }
    protected bool Vertical => Window.Vertical;

    protected BarPresentation(BarService service, BarWindow window)
    {
        Service = service;
        Window = window;
    }

    /// <summary>Rebuilds the bar's contents from the current window list.</summary>
    public abstract void Refresh();

    private BarTray? _tray;
    private BarClock? _clock;

    /// <summary>
    /// The tray and the clock survive a rebuild.
    ///
    /// Refresh runs whenever any window anywhere opens, closes, is renamed or
    /// changes which one is in front, and a fresh tray would re-read the real
    /// notification area every single time — which means driving UI Automation
    /// over Explorer's taskbar several times a second. They are made once and
    /// moved into the new tree instead, so the tray keeps to its own schedule
    /// and the clock never restarts mid-second.
    /// </summary>
    protected BarTray Tray(double size) => Detach(_tray ??= new BarTray(Vertical, size));

    protected BarClock Clock(bool showDate = true) => Detach(_clock ??= new BarClock(Vertical, showDate));

    private readonly Dictionary<string, (double Height, bool Dark, FrameworkElement View)> _applets = [];

    /// <summary>
    /// The applets survive a rebuild too, for the same reason the tray does.
    ///
    /// An applet is a whole widget, and a fresh one starts its widget over:
    /// the Shelf re-reads its folder and builds a tile per file on the way in.
    /// Paying that on every window title change anywhere on the desktop is
    /// what a bar refresh would otherwise cost. They are made once and moved
    /// into the new tree, and made again only if the bar's thickness or the
    /// theme moved under them. A change to the applet list goes through
    /// SaveAndApply, which replaces the presentation and this cache with it.
    /// </summary>
    protected IEnumerable<FrameworkElement> Applets(double height)
    {
        var dark = !StartStyle.IsLight;
        foreach (var id in Settings.Applets)
        {
            if (!_applets.TryGetValue(id, out var applet) || applet.Height != height || applet.Dark != dark)
            {
                if (Service.BuildApplet(id, height) is not { } view) continue;
                applet = (height, dark, view);
                _applets[id] = applet;
            }
            yield return Detach(applet.View);
        }
    }

    /// <summary>Takes an element out of the tree it is in, so it can be put in the new one.</summary>
    protected static T Detach<T>(T element) where T : FrameworkElement
    {
        switch (element.Parent)
        {
            case Panel panel: panel.Children.Remove(element); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl host: host.Content = null; break;
        }
        return element;
    }

    /// <summary>
    /// Fills a strip with one section: the bar's own widgets for that section,
    /// then whatever buttons belong there, with a separator where two runs meet.
    ///
    /// Every style is built from these, which is what lets a stack or a spacer
    /// be dropped into any of them without each style knowing about it.
    /// </summary>
    protected void AddSection(StackPanel strip, BarSection section, IEnumerable<FrameworkElement>? buttons = null)
    {
        var widgets = Service.WidgetsIn(section).Where(Accepts).ToList();
        foreach (var widget in widgets)
        {
            foreach (var element in BarWidgetFactory.Build(Service, widget, Settings.Thickness, Window.Edge))
                strip.Children.Add(element);
        }

        var list = buttons?.ToList() ?? [];
        if (list.Count == 0) return;
        if (widgets.Count > 0 && Settings.ShowSeparators) strip.Children.Add(Separator());
        foreach (var button in list) strip.Children.Add(button);
    }

    /// <summary>
    /// Whether this style shows a given bar widget.
    ///
    /// Folders belong in a dock. They are a dock's idea — a pile you pull
    /// items out of — and on a taskbar or a panel they read as a stray button
    /// nobody asked for, which is what the user said. The base class says no;
    /// the dock and the menu bar say yes.
    /// </summary>
    protected virtual bool Accepts(BarWidget widget) =>
        widget.Kind is not (BarWidgetKind.Stack or BarWidgetKind.DesktopStack);

    protected BarSeparator Separator() => new(Vertical, Settings.Thickness);

    /// <summary>A button for every window, or one per application when they are grouped.</summary>
    protected IEnumerable<FrameworkElement> Buttons(IEnumerable<BarAppGroup> groups, double icon,
        bool showTitle, double radius = 8)
    {
        foreach (var group in groups)
        {
            if (Settings.GroupWindows || group.Windows.Count <= 1)
            {
                yield return new BarButton(Service, group, icon, showTitle, Window.Edge, radius);
                continue;
            }
            // Ungrouped: every window is its own button, so each keeps its own
            // title rather than the application's name.
            foreach (var entry in group.Windows)
                yield return new BarButton(Service, group with { Windows = [entry] }, icon, showTitle, Window.Edge, radius);
        }
    }

    /// <summary>The groups this bar should show: its own display's, and pinned apps that are not running.</summary>
    protected IReadOnlyList<BarAppGroup> Groups()
    {
        var groups = Service.Windows.Groups;
        if (!Settings.WindowsOfThisDisplayOnly || Service.BarCount < 2) return groups;

        var monitor = (int)Window.Display.Handle;
        return groups
            .Select(g => g with { Windows = g.Windows.Where(w => w.Monitor == monitor).ToList() })
            .Where(g => g.Windows.Count > 0)
            .ToList();
    }

    protected StackPanel Strip(double gap = 0) => new()
    {
        Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal,
        VerticalAlignment = Vertical ? VerticalAlignment.Top : VerticalAlignment.Center,
        HorizontalAlignment = Vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        Margin = new Thickness(gap),
    };

    protected FrameworkElement StartButton(double size)
    {
        var button = new PressableBorder(radius: 8)
        {
            Padding = new Thickness(8, 6, 8, 6),
            ToolTip = "Start",
            VerticalAlignment = VerticalAlignment.Center,
            Child = StartStyle.GlyphText(StartStyle.Glyph(0xF0E2), size, StartStyle.Text),
        };
        button.Clicked += _ => Service.OpenStart();
        button.RightClicked += _ => Service.ShowBarMenu(button);
        // Remembered so the Start menu can come out of it rather than out of
        // the middle of the screen.
        Window.StartButton = button;
        return button;
    }

    /// <summary>Ends pinned to their edges, the window list filling what is left and scrolling if it has to.</summary>
    /// <param name="centre">
    /// Centre the middle group on the bar as a whole rather than in the space
    /// between the ends — which is how Windows 11's own centred taskbar reads,
    /// and the only way the icons line up with the middle of the screen.
    /// </param>
    protected FrameworkElement Layout(UIElement start, UIElement middle, UIElement end, bool centre = false)
    {
        var scroll = new ScrollViewer
        {
            Content = middle,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            PanningMode = Vertical ? PanningMode.VerticalOnly : PanningMode.HorizontalOnly,
        };
        if (Vertical) scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var grid = new Grid { Margin = new Thickness(Vertical ? 2 : 6, Vertical ? 6 : 2, Vertical ? 2 : 6, Vertical ? 6 : 2) };
        if (Vertical)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(scroll, 1);
            Grid.SetRow(end, 2);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(scroll, 1);
            Grid.SetColumn(end, 2);
        }
        if (centre)
        {
            // Across the whole bar, centred, with the ends drawn over it.
            if (Vertical)
            {
                Grid.SetRow(scroll, 0);
                Grid.SetRowSpan(scroll, 3);
                scroll.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                Grid.SetColumn(scroll, 0);
                Grid.SetColumnSpan(scroll, 3);
                scroll.HorizontalAlignment = HorizontalAlignment.Center;
            }
        }

        grid.Children.Add(scroll);
        grid.Children.Add((UIElement)start);
        grid.Children.Add(end);
        return grid;
    }

    /// <summary>
    /// Opens Windows' own quick settings.
    ///
    /// Hearth had a control centre of its own for a while and it was not worth
    /// keeping: Windows' panel already has the brightness and volume sliders,
    /// the real network and Bluetooth flyouts, Cast, Night light, accessibility
    /// and battery, all of them live and all of them wired to the system. A
    /// reimplementation would be a worse copy of every one. So the button is a
    /// button for the real thing.
    /// </summary>
    protected FrameworkElement ControlCentreButton()
    {
        var button = new PressableBorder(radius: 6)
        {
            Padding = new Thickness(8, 3, 8, 3),
            ToolTip = "Quick settings",
            VerticalAlignment = VerticalAlignment.Center,
            Child = StartStyle.GlyphText(StartStyle.Glyph(0xE9E9), 14, StartStyle.Text),
        };
        button.Clicked += b => ShellFlyouts.QuickSettings(b, Window.Edge);
        return button;
    }

    protected FrameworkElement ShowDesktopButton()
    {
        // A thin strip at the very end, the way Windows has always had one.
        var button = new PressableBorder(radius: 3)
        {
            Width = Vertical ? double.NaN : 8,
            Height = Vertical ? 8 : double.NaN,
            ToolTip = "Show the desktop",
            Background = Brushes.Transparent,
            Margin = new Thickness(Vertical ? 0 : 3, Vertical ? 3 : 0, 0, 0),
        };
        button.Clicked += _ => Service.ShowDesktop();
        return button;
    }
}

/// <summary>
/// The Windows arrangement: Start, then one labelled button per window (or per
/// application when they are grouped), then the tray and the clock. The one
/// every Windows user already knows how to drive.
/// </summary>
internal sealed class TaskbarPresentation(BarService service, BarWindow window) : BarPresentation(service, window)
{
    public override void Refresh()
    {
        var thickness = Settings.Thickness;
        var icon = Math.Round(thickness * 0.42);
        var items = Service.ItemsFor(Groups());

        var left = Strip();
        if (Settings.ShowStart) left.Children.Add(StartButton(Math.Round(thickness * 0.34)));
        AddSection(left, BarSection.Start);

        var middle = Strip();
        foreach (var button in Buttons(items.Pinned, icon, Settings.ShowTitles)) middle.Children.Add(button);
        AddSection(middle, BarSection.Middle);
        // The line the user asked for: pinned on one side of it, open on the
        // other, so a bar with both reads as two runs rather than one.
        if (items.HasBoth && Settings.ShowSeparators) middle.Children.Add(Separator());
        foreach (var button in Buttons(items.Running, icon, Settings.ShowTitles)) middle.Children.Add(button);

        var right = Strip();
        right.HorizontalAlignment = Vertical ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        right.VerticalAlignment = Vertical ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        AddSection(right, BarSection.End);
        if (Settings.ShowTray) right.Children.Add(Tray(Math.Round(thickness * 0.34)));
        if (Settings.ShowClock) right.Children.Add(Clock());
        if (Settings.ShowShowDesktop) right.Children.Add(ShowDesktopButton());

        Window.Surface.Child = Layout(left, middle, right);
    }
}

/// <summary>
/// The Linux panel.
///
/// The difference from a taskbar is not decoration, it is what the thing is
/// for. A taskbar is a list of your windows with a clock bolted on. A panel is
/// a rail you bolt things to, and the window list is only one of them — which
/// is why this one has a named launcher button rather than an orb, square
/// corners rather than rounded, every applet in its own compartment with a
/// rule between them, and the window list given titles and left to fill
/// whatever is left over. Hearth's own widgets are the applets.
/// </summary>
internal sealed class PanelPresentation(BarService service, BarWindow window) : BarPresentation(service, window)
{
    public override void Refresh()
    {
        var thickness = Settings.Thickness;
        var icon = Math.Round(thickness * 0.44);
        var items = Service.ItemsFor(Groups());

        var left = Strip();
        if (Settings.ShowStart) left.Children.Add(MenuButton(thickness));
        AddSection(left, BarSection.Start);
        if (items.Pinned.Count > 0)
        {
            if (Settings.ShowSeparators) left.Children.Add(Separator());
            foreach (var button in Buttons(items.Pinned, icon, showTitle: false, radius: 3))
                left.Children.Add(button);
        }

        // The task list: titles, square, filling the rail.
        var tasks = Strip();
        foreach (var button in Buttons(items.Running, icon * 0.8, Settings.ShowTitles, radius: 3))
        {
            if (button is FrameworkElement element && !Vertical && Settings.ShowTitles) element.MinWidth = 120;
            tasks.Children.Add(button);
        }
        AddSection(tasks, BarSection.Middle);

        // Applets, each in its own compartment with a rule before it — the
        // thing that makes a panel look like a panel.
        var right = Strip();
        foreach (var applet in Applets(Math.Round(thickness) - 10))
            right.Children.Add(Compartment(applet));
        AddSection(right, BarSection.End);
        if (Settings.ShowTray) right.Children.Add(Compartment(Tray(Math.Round(thickness * 0.34))));
        if (Settings.ShowClock) right.Children.Add(Compartment(Clock()));

        Window.Surface.Child = Layout(left, tasks, right);
    }

    /// <summary>A named menu button, the way every Linux panel opens its applications.</summary>
    private FrameworkElement MenuButton(double thickness)
    {
        var label = StartStyle.Label("Applications", 12, StartStyle.Text, FontWeights.SemiBold);
        label.Margin = new Thickness(7, 0, 0, 0);
        label.VerticalAlignment = VerticalAlignment.Center;

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(StartStyle.GlyphText(StartStyle.Glyph(0xE8A5), Math.Round(thickness * 0.32), StartStyle.Accent));
        if (!Vertical) row.Children.Add(label);

        var button = new PressableBorder(radius: 3)
        {
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = row,
        };
        button.Clicked += _ => Service.OpenStart("all");
        button.RightClicked += b => Service.ShowBarMenu(b);
        return button;
    }

    private FrameworkElement Compartment(FrameworkElement content) => new Border
    {
        Child = content,
        Padding = Vertical ? new Thickness(0, 5, 0, 5) : new Thickness(7, 0, 7, 0),
        BorderBrush = StartStyle.CardEdge,
        BorderThickness = Vertical ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0),
    };
}

/// <summary>
/// The Mac arrangement, as two separate bars: a thin menu bar along the top
/// that holds the status items (tray, clock) and the name of whatever is in
/// front, and a dock floating at the other edge holding the apps, which grows
/// under the pointer.
///
/// The menu bar is the one that reserves space; the dock floats over the
/// windows, because a dock that pushed everything up would only be a fat
/// taskbar.
/// </summary>
internal sealed class MenuBarPresentation(BarService service, BarWindow window) : BarPresentation(service, window)
{
    /// <summary>Half of the dock arrangement, so it takes folders too.</summary>
    protected override bool Accepts(BarWidget widget) => true;

    public override void Refresh()
    {
        var left = Strip();
        if (Settings.ShowStart)
        {
            var ember = new PressableBorder(radius: 6)
            {
                Padding = new Thickness(10, 2, 10, 2),
                ToolTip = "Start",
                Child = StartStyle.GlyphText(StartStyle.Glyph(0xECAD), 14, StartStyle.Accent),
            };
            ember.Clicked += _ => Service.OpenStart();
            ember.RightClicked += _ => Service.ShowBarMenu(ember);
            left.Children.Add(ember);
        }

        // The name of whatever is in front, in bold, exactly where a Mac user
        // expects to find out what they are typing into.
        var front = Service.Windows.Groups.FirstOrDefault(g => g.Foreground);
        var name = StartStyle.Label(front?.Program ?? "Hearth", 12.5, StartStyle.Text, FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(4, 0, 10, 0);
        left.Children.Add(name);

        if (front is not null)
        {
            var windows = new PressableBorder(radius: 5)
            {
                Padding = new Thickness(7, 2, 7, 2),
                Child = StartStyle.Label($"Windows ({front.Windows.Count})", 12, StartStyle.Muted),
            };
            windows.Clicked += b => Service.ShowAppMenu(b, front);
            left.Children.Add(windows);
        }

        var right = Strip();
        right.HorizontalAlignment = HorizontalAlignment.Right;
        AddSection(right, BarSection.End);
        if (Settings.ShowTray) right.Children.Add(Tray(15));
        right.Children.Add(ControlCentreButton());
        if (Settings.ShowClock) right.Children.Add(Clock(showDate: false));

        var grid = new Grid { Margin = new Thickness(6, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Window.Surface.Child = grid;
    }
}

/// <summary>The dock half of <see cref="MenuBarPresentation"/>: apps only, magnified under the pointer.</summary>
internal sealed class DockPresentation(BarService service, BarWindow window) : BarPresentation(service, window)
{
    /// <summary>The dock is where folders belong; it takes everything.</summary>
    protected override bool Accepts(BarWidget widget) => true;

    public override void Refresh()
    {
        var thickness = Settings.Thickness;
        var icon = Math.Round(thickness * 0.62);
        var row = Strip();
        row.HorizontalAlignment = HorizontalAlignment.Center;
        row.VerticalAlignment = VerticalAlignment.Center;
        row.Margin = new Thickness(8, 0, 8, 0);

        var items = Service.ItemsFor(Groups());
        foreach (var group in items.Pinned) Add(group);
        AddSection(row, BarSection.Start);
        if (items.HasBoth && Settings.ShowSeparators) row.Children.Add(Separator());
        foreach (var group in items.Running.Where(g => !g.Minimized)) Add(group);

        // Minimised windows get their own run at the end, the way a dock keeps
        // what you have put away separate from what you are working in.
        var minimised = items.Running.Where(g => g.Minimized).ToList();
        if (minimised.Count > 0)
        {
            if (Settings.ShowSeparators) row.Children.Add(Separator());
            foreach (var group in minimised) Add(group);
        }

        AddSection(row, BarSection.End);

        Window.Surface.Child = row;
        Window.Resize();
        return;

        void Add(BarAppGroup group)
        {
            var button = new BarButton(Service, group, icon, showTitle: false, Window.Edge, radius: 10);
            Magnify(button);
            row.Children.Add(button);
        }
    }

    /// <summary>
    /// The one thing everyone remembers about a dock. Done with a render
    /// transform rather than by resizing, so the strip's layout never changes
    /// and the window does not have to be re-placed on every mouse move.
    /// </summary>
    private void Magnify(FrameworkElement button)
    {
        var scale = new ScaleTransform(1, 1);
        button.RenderTransform = scale;
        button.RenderTransformOrigin = new Point(0.5, Vertical ? 0.5 : 1.0);

        var grow = Math.Clamp(Settings.DockMagnification, 1.0, 2.0);
        button.MouseEnter += (_, _) => Animate(scale, grow);
        button.MouseLeave += (_, _) => Animate(scale, 1.0);
    }

    private static void Animate(ScaleTransform scale, double to)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}

/// <summary>
/// One square per application rather than per window — the Docklike idea from
/// the Linux world. No titles, no growing and shrinking as windows open: the
/// bar is a stable row of applications you learn the positions of, and a click
/// walks that application's windows.
///
/// The dots under a square say how many windows there are, which is the whole
/// of the extra information a grouped bar has to convey.
/// </summary>
internal sealed class DocklikePresentation(BarService service, BarWindow window) : BarPresentation(service, window)
{
    public override void Refresh()
    {
        var thickness = Settings.Thickness;
        var icon = Math.Round(thickness * 0.5);
        var items = Service.ItemsFor(Groups());

        var left = Strip();
        var squares = Strip();
        if (Settings.ShowStart) squares.Children.Add(StartButton(Math.Round(thickness * 0.32)));
        AddSection(squares, BarSection.Start);
        foreach (var group in items.Pinned) squares.Children.Add(Square(group, icon, thickness));
        AddSection(squares, BarSection.Middle);
        if (items.HasBoth && Settings.ShowSeparators) squares.Children.Add(Separator());
        foreach (var group in items.Running) squares.Children.Add(Square(group, icon, thickness));

        var right = Strip();
        right.HorizontalAlignment = Vertical ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        right.VerticalAlignment = Vertical ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        AddSection(right, BarSection.End);
        if (Settings.ShowTray) right.Children.Add(Tray(Math.Round(thickness * 0.3)));
        if (Settings.ShowClock) right.Children.Add(Clock());

        Window.Surface.Child = Layout(left, squares, right, centre: true);
    }

    private FrameworkElement Square(BarAppGroup group, double icon, double thickness)
    {
        var image = BarIcons.Build(group, icon, VisualTreeHelper.GetDpi(Window).PixelsPerDip);
        image.Opacity = group.Windows.Count == 0 ? 0.45 : group.Minimized ? 0.7 : 1;

        // Against the side facing the screen edge, like the bar buttons' own
        // indicator.
        var dots = new StackPanel
        {
            Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal,
            HorizontalAlignment = Window.Edge switch
            {
                ScreenEdge.Left => HorizontalAlignment.Left,
                ScreenEdge.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center,
            },
            VerticalAlignment = Window.Edge switch
            {
                ScreenEdge.Top => VerticalAlignment.Top,
                ScreenEdge.Bottom => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Center,
            },
            IsHitTestVisible = false,
        };
        // Three is as many as anyone reads at a glance; past that the count is
        // in the tooltip.
        for (var i = 0; i < Math.Min(group.Windows.Count, 3); i++)
        {
            dots.Children.Add(new Border
            {
                Width = 2.5,
                Height = 2.5,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                CornerRadius = new CornerRadius(1.25),
                Background = group.Foreground ? BarStyleSheet.Running : BarStyleSheet.Idle,
                Opacity = group.Foreground ? 1 : 0.6,
            });
        }

        var button = new BarButton(Service, group, icon, showTitle: false, Window.Edge, radius: 6)
        {
            Width = Vertical ? double.NaN : Math.Round(thickness * 0.92),
            Height = Vertical ? Math.Round(thickness * 0.92) : double.NaN,
        };
        // BarButton draws its own indicator bar; the squares use dots instead.
        if (button.Child is Grid content)
        {
            content.Children.Clear();
            content.Children.Add(image);
            content.Children.Add(dots);
        }
        return button;
    }
}
