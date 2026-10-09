using System.Windows;
using System.Windows.Controls;
using Hearth.App.Bar;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;
using static Hearth.App.Views.SettingsScreen.SettingsStyle;

namespace Hearth.App.Views.SettingsScreen;

/// <summary>
/// The bar's settings page. The bar is off by default and is not needed to run
/// Hearth, so the page leads with the switch and shows nothing else until it
/// is on.
/// </summary>
internal sealed partial class SettingsWindow
{
    private FrameworkElement BuildBarPage()
    {
        var page = new StackPanel();
        page.Children.Add(Heading("Hearth bar"));

        if (BarService.Current is not { } bar)
        {
            page.Children.Add(Note("The bar is not running in this copy of Hearth.", Warn));
            return page;
        }

        var s = bar.Settings;
        void Apply() => bar.SaveAndApply();

        page.Children.Add(Card(
            ToggleRow("Use Hearth's own bar",
                "Off by default. With it on, Hearth can also take the Windows taskbar off the screen; " +
                "it is put back whenever the bar is turned off or Hearth closes.",
                s.Enabled, v => { bar.SetEnabled(v); ShowPage("bar"); })));

        if (!s.Enabled)
        {
            page.Children.Add(Note("Turn the bar on to choose its style."));
            return page;
        }

        page.Children.Add(Section("Style"));
        page.Children.Add(Card(
            ChoiceRow("Bar style", StyleDescription(s.Style),
                BarService.BarStyleNames(), s.Style,
                v =>
                {
                    s.Style = v;
                    var (min, max, preferred) = s.ThicknessRange();
                    if (s.Thickness < min || s.Thickness > max) s.Thickness = preferred;
                    Apply();
                    ShowPage("bar");
                }, wrap: true)));

        page.Children.Add(Section("Placement"));
        var (thinnest, thickest, _) = s.ThicknessRange();
        var placement = new List<FrameworkElement>();

        if (s.Style != BarStyle.MenuBarAndDock && s.Style != BarStyle.Halo)
        {
            placement.Add(ChoiceRow("Edge", null,
                new[] { (ScreenEdge.Bottom, "Bottom"), (ScreenEdge.Top, "Top"), (ScreenEdge.Left, "Left"), (ScreenEdge.Right, "Right") },
                s.Edge, v => { s.Edge = v; Apply(); }));
        }

        if (s.Style == BarStyle.Halo)
        {
            placement.Add(SliderRow("Ring thickness", null, thinnest, thickest, 1, s.HaloThickness,
                v => $"{v:F0}", v => { s.HaloThickness = v; Apply(); }));
            placement.Add(SliderRow("Thickness when you rest on it",
                "Also how far from the edge the pointer counts as being on the ring.",
                6, 24, 1, s.HaloActiveThickness, v => $"{v:F0}", v => { s.HaloActiveThickness = v; Apply(); }));
            placement.Add(ToggleRow("Longer arcs for apps with more windows", null, s.HaloWeighByWindows,
                v => { s.HaloWeighByWindows = v; Apply(); }));
            placement.Add(EdgesRow(s, Apply));
        }
        else
        {
            placement.Add(SliderRow("Thickness", null, thinnest, thickest, 2, s.Thickness,
                v => $"{v:F0}", v => { s.Thickness = v; Apply(); }));
        }

        if (s.Style == BarStyle.MenuBarAndDock)
        {
            placement.Add(ChoiceRow("Dock shape", null,
                new[]
                {
                    (DockShape.Pill, "Pill"), (DockShape.Rounded, "Rounded"),
                    (DockShape.Subtle, "Barely rounded"), (DockShape.Square, "Square"),
                }, s.Shape, v => { s.Shape = v; Apply(); }, wrap: true));
        }

        placement.Add(ToggleRow("A bar on every display", null, s.AllDisplays, v => { s.AllDisplays = v; Apply(); }));
        placement.Add(ToggleRow("Each bar shows only its own display's windows", null,
            s.WindowsOfThisDisplayOnly, v => { s.WindowsOfThisDisplayOnly = v; Apply(); }));
        page.Children.Add(Card([.. placement]));

        page.Children.Add(Section("What is on it"));
        var parts = new List<FrameworkElement>
        {
            ToggleRow("Start button", null, s.ShowStart, v => { s.ShowStart = v; Apply(); }),
            ToggleRow("Notification area", "The Windows tray icons, shown at Hearth's size. Clicks and menus go to the real icon.",
                s.ShowTray, v => { s.ShowTray = v; Apply(); }),
            ToggleRow("Clock", null, s.ShowClock, v => { s.ShowClock = v; Apply(); }),
        };
        if (s.Style is BarStyle.Taskbar or BarStyle.Panel)
        {
            parts.Add(ToggleRow("Titles beside the icons", null, s.ShowTitles, v => { s.ShowTitles = v; Apply(); }));
            parts.Add(ToggleRow("One button per application", "Otherwise one for every window.",
                s.GroupWindows, v => { s.GroupWindows = v; Apply(); }));
        }
        if (s.Style is BarStyle.Taskbar or BarStyle.Docklike)
            parts.Add(ToggleRow("Show desktop corner", null, s.ShowShowDesktop, v => { s.ShowShowDesktop = v; Apply(); }));
        if (s.Style == BarStyle.MenuBarAndDock)
            parts.Add(SliderRow("How much the dock grows under the pointer", null, 1.0, 2.0, 0.05,
                s.DockMagnification, v => $"{v:F2}x", v => { s.DockMagnification = v; Apply(); }));
        if (s.Style == BarStyle.Panel) parts.Add(AppletsRow(s, Apply));
        page.Children.Add(Card([.. parts]));

        page.Children.Add(Section("Appearance"));
        page.Children.Add(Card(
            ToggleRow("Blur what is behind it",
                "Hearth takes a picture of what is behind the bar and blurs it itself, the same way the Start menu " +
                "does — Windows' own blur does nothing when transparency effects are off, which Energy Saver does.",
                s.Blur, v => { s.Blur = v; Apply(); }),
            SliderRow("How solid", "Over that blur. A flat bar ignores this.", 0.3, 1.0, 0.05, s.Opacity,
                v => $"{v * 100:F0}%", v => { s.Opacity = v; Apply(); }),
            ToggleRow("A line between pinned and open apps", null, s.ShowSeparators,
                v => { s.ShowSeparators = v; Apply(); })));

        page.Children.Add(Section("Hiding"));
        var hiding = new List<FrameworkElement>();
        if (s.Style == BarStyle.MenuBarAndDock)
        {
            hiding.Add(ToggleRow("Hide the menu bar until the pointer reaches the top", null,
                s.MenuBarAutoHide, v => { s.MenuBarAutoHide = v; Apply(); }));
            hiding.Add(ToggleRow("Hide the dock until the pointer reaches the bottom", null,
                s.DockAutoHide, v => { s.DockAutoHide = v; Apply(); }));
            hiding.Add(HidingRows(s, Apply));
        }
        else if (s.Style == BarStyle.Halo)
        {
            hiding.Add(ChoiceRow("The pill", "The corner holding Start, the tray and the clock.",
                new[]
                {
                    (PillSize.Full, "Always there"),
                    (PillSize.Shrink, "Shrink to a dot"),
                    (PillSize.Hidden, "Hide completely"),
                }, s.Pill, v => { s.Pill = v; Apply(); }));
            hiding.Add(ChoiceRow("Which corner", null,
                new[]
                {
                    (Corner.BottomRight, "Bottom right"), (Corner.BottomLeft, "Bottom left"),
                    (Corner.TopRight, "Top right"), (Corner.TopLeft, "Top left"),
                }, s.PillCorner, v => { s.PillCorner = v; Apply(); }, wrap: true));
            hiding.Add(SliderRow("Pill size", null, 0.6, 1.6, 0.05, s.PillScale, v => $"{v * 100:F0}%",
                v => { s.PillScale = v; Apply(); }));
            hiding.Add(SliderRow("Pill gap from the corner", null, 0, 60, 2, s.PillMargin, v => $"{v:F0}",
                v => { s.PillMargin = v; Apply(); }));
        }
        else
        {
            hiding.Add(ToggleRow("Hide until the pointer reaches its edge",
                "The strip stays reserved, so your windows do not resize every time it hides.",
                s.AutoHide, v => { s.AutoHide = v; Apply(); }));
            hiding.Add(HidingRows(s, Apply));
        }
        page.Children.Add(Card([.. hiding]));

        if (s.Style != BarStyle.Halo)
        {
            page.Children.Add(Section("On the bar itself"));
            page.Children.Add(Card(
                Row("Stacks, shortcuts and spacers",
                    "A stack is a folder that opens on the bar. Right-click anything you add for its own options.",
                    AddRow(bar)),
                WidgetList(bar)));
        }

        page.Children.Add(Section("The Windows taskbar"));
        page.Children.Add(Card(
            ChoiceRow("While the Hearth bar is running",
                "Hidden means the taskbar is given an empty window region: nothing of it is drawn and clicks pass " +
                "straight through, but it stays fully alive, so the tray icons and the hidden-icon flyout still work " +
                "and Hearth can read them without the bar flashing on screen.",
                new[] { (ShellTaskbarHandling.Hide, "Hidden"), (ShellTaskbarHandling.Leave, "Left alone") },
                s.ShellTaskbar, v => { s.ShellTaskbar = v; Apply(); })));

        if (ShellTaskbar.StripStillReserved)
        {
            page.Children.Add(Card(
                Row("Windows is still holding the taskbar's space",
                    "Hearth can make the Windows taskbar invisible and click-through, but only Windows itself can give " +
                    "back the strip it reserves — and on Windows 11 that setting is only read when Explorer loads. " +
                    "Hearth has written it; restarting Explorer applies it now. Open File Explorer windows will close.",
                    new WrapPanel
                    {
                        Children =
                        {
                            Button("Restart Explorer now", () => ShellTaskbar.RestartExplorer()),
                            Button("Open Windows taskbar settings",
                                () => Hearth.Core.Shell.ShellLauncher.Open("ms-settings:taskbar")),
                        },
                    }, wrapControl: true)));
        }

        page.Children.Add(Note("Pin an app by right-clicking its button on the bar. " +
                               "The taskbar is restored whenever Hearth closes, and a watchdog restores it if Hearth is killed."));
        return page;
    }

    /// <summary>How a hiding bar behaves, shared by every style that can hide.</summary>
    private static FrameworkElement HidingRows(BarSettings s, Action apply)
    {
        var panel = new StackPanel();
        panel.Children.Add(ChoiceRow("When it hides", null,
            new[]
            {
                (HideStyle.Always, "Always, until you reach for it"),
                (HideStyle.WhenCovered, "Only when a window covers it"),
            }, s.Hiding, v => { s.Hiding = v; apply(); }));
        panel.Children.Add(SliderRow("How fast it slides", "Zero is instant.", 0, 400, 20, s.HideAnimationMs,
            v => v <= 0 ? "instant" : $"{v:F0} ms", v => { s.HideAnimationMs = (int)v; apply(); }));
        return panel;
    }

    /// <summary>The buttons that put something new on the bar.</summary>
    private static FrameworkElement AddRow(BarService bar)
    {
        // Bounded, or the buttons take the whole row and squeeze the title
        // column down to one character per line.
        var row = new WrapPanel { MaxWidth = 420 };
        foreach (var (label, make) in new (string, Action)[]
                 {
                     ("Add a folder...", () =>
                     {
                         var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder for the bar" };
                         if (dialog.ShowDialog() == true) bar.AddWidget(BarWidgetKind.Stack, dialog.FolderName);
                     }),
                     ("Add the desktop", () => bar.AddWidget(BarWidgetKind.DesktopStack)),
                     ("Add the Recycle Bin", () => bar.AddWidget(BarWidgetKind.RecycleBin)),
                     ("Add a spacer", () => bar.AddWidget(BarWidgetKind.Spacer)),
                 })
        {
            var run = make;
            var button = Button(label, () =>
            {
                try { run(); }
                catch (Exception ex) { Log.Error($"bar: {label}", ex); }
            });
            button.Margin = new Thickness(0, 0, 8, 6);
            row.Children.Add(button);
        }
        return row;
    }

    /// <summary>What is already on it, with a way to take each one off.</summary>
    private static FrameworkElement WidgetList(BarService bar)
    {
        var list = new StackPanel();
        if (bar.Settings.Widgets.Count == 0)
        {
            list.Children.Add(Note("Nothing added yet."));
            return list;
        }

        foreach (var widget in bar.Settings.Widgets.ToList())
        {
            var entry = widget;
            var name = entry.Name ?? (entry.Kind switch
            {
                BarWidgetKind.DesktopStack => "Desktop",
                BarWidgetKind.Spacer => "Spacer",
                _ => BarWidgetFactory.NameOf(entry.Target),
            });
            var remove = Button("Remove", () =>
            {
                bar.Settings.Widgets.RemoveAll(w => w.Id == entry.Id);
                bar.SaveAndApply();
            });
            list.Children.Add(Row($"{name}", $"{entry.Kind}, {entry.Section}", remove));
        }
        return list;
    }

    private static string StyleDescription(BarStyle style) => style switch
    {
        BarStyle.Taskbar => "One button per window, a tray and a clock, the way Windows does it.",
        BarStyle.Panel => "A Linux panel: a menu button, a window list, and Hearth widgets as applets.",
        BarStyle.MenuBarAndDock => "A thin menu bar along the top and a separate dock that grows under the pointer.",
        BarStyle.Docklike => "One square per application, grouped, with a dot for each window.",
        BarStyle.Halo => "No bar at all. Every running app owns an arc of the screen's border, coloured by its own " +
                         "icon; rest the pointer on the edge to bloom one open. It takes no screen space and moves nothing.",
        _ => string.Empty,
    };

    /// <summary>Halo only: which sides of the screen carry the ring.</summary>
    private static FrameworkElement EdgesRow(BarSettings s, Action apply)
    {
        var row = new WrapPanel();
        foreach (var (edge, label) in new[]
                 {
                     (ScreenEdge.Top, "Top"), (ScreenEdge.Right, "Right"),
                     (ScreenEdge.Bottom, "Bottom"), (ScreenEdge.Left, "Left"),
                 })
        {
            var chosen = edge;
            var box = new CheckBox
            {
                Content = label,
                Margin = new Thickness(0, 0, 14, 0),
                IsChecked = s.HaloEdges.Count == 0 || s.HaloEdges.Contains(edge),
                Foreground = Muted,
            };
            box.Checked += (_, _) => Set(chosen, true);
            box.Unchecked += (_, _) => Set(chosen, false);
            row.Children.Add(box);
        }
        return Row("Sides", "The ring runs round whichever sides are ticked. Leaving the top off keeps a maximized " +
                            "window's close button clear of it.", row, wrapControl: true);

        void Set(ScreenEdge edge, bool on)
        {
            // Empty means all four, so the list is filled in the moment one is
            // turned off and a single change stops meaning "all of them".
            if (s.HaloEdges.Count == 0)
                s.HaloEdges = [ScreenEdge.Top, ScreenEdge.Right, ScreenEdge.Bottom, ScreenEdge.Left];
            if (on) { if (!s.HaloEdges.Contains(edge)) s.HaloEdges.Add(edge); }
            else s.HaloEdges.Remove(edge);
            if (s.HaloEdges.Count == 0) s.HaloEdges.Add(ScreenEdge.Bottom);
            apply();
        }
    }

    /// <summary>Panel only: which widgets sit in the strip as applets.</summary>
    private static FrameworkElement AppletsRow(BarSettings s, Action apply)
    {
        var row = new WrapPanel();
        foreach (var widget in Widgets.WidgetRegistry.All.OrderBy(w => w.Order))
        {
            var id = widget.Id;
            var box = new CheckBox
            {
                Content = widget.Title,
                Margin = new Thickness(0, 0, 14, 4),
                IsChecked = s.Applets.Contains(id),
                Foreground = Muted,
            };
            box.Checked += (_, _) => { if (!s.Applets.Contains(id)) { s.Applets.Add(id); apply(); } };
            box.Unchecked += (_, _) => { if (s.Applets.Remove(id)) apply(); };
            row.Children.Add(box);
        }
        return Row("Applets", "Hearth widgets shrunk into the panel. A widget with a lot in it will not read at this size.",
            row, wrapControl: true);
    }
}
