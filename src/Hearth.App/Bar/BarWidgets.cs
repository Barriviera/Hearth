using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Hearth.App.Services;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;
using Hearth.Core.Shell;

namespace Hearth.App.Bar;

/// <summary>
/// A thin line between two runs of buttons.
///
/// Every bar here divides into sections — pinned apps, then what is running,
/// then whatever else has been put on it — and without a mark between them the
/// bar reads as one undifferentiated row. A one-pixel line is the whole of what
/// is needed; docks have used it for twenty years.
/// </summary>
internal sealed class BarSeparator : Border
{
    public BarSeparator(bool vertical, double thickness)
    {
        var length = Math.Round(thickness * 0.55);
        if (vertical)
        {
            Height = 1;
            Width = length;
            Margin = new Thickness(0, 5, 0, 5);
        }
        else
        {
            Width = 1;
            Height = length;
            Margin = new Thickness(6, 0, 6, 0);
        }
        Background = StartStyle.CardEdge;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        IsHitTestVisible = false;
    }
}

/// <summary>Builds the elements for a bar's own widgets.</summary>
internal static class BarWidgetFactory
{
    public static IEnumerable<FrameworkElement> Build(BarService service, BarWidget widget, double thickness, ScreenEdge edge)
    {
        FrameworkElement? element = null;
        try
        {
            element = widget.Kind switch
            {
                BarWidgetKind.Spacer => Spacer(widget, edge),
                BarWidgetKind.Shortcut => Shortcut(service, widget, thickness, edge),
                BarWidgetKind.Widget => Widget(service, widget, thickness),
                BarWidgetKind.RecycleBin => new BarTrash(service, Math.Round(thickness * 0.5)),
                _ => new StackButton(service, widget, thickness, edge),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"bar widget '{widget.Kind}'", ex);
        }
        if (element is not null) yield return element;
    }

    private static FrameworkElement Spacer(BarWidget widget, ScreenEdge edge)
    {
        var vertical = edge is ScreenEdge.Left or ScreenEdge.Right;
        var size = widget.Size > 0 ? widget.Size : 24;
        return new Border
        {
            Width = vertical ? 1 : size,
            Height = vertical ? size : 1,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
        };
    }

    private static FrameworkElement Shortcut(BarService service, BarWidget widget, double thickness, ScreenEdge edge)
    {
        var item = App.Apps.Find(widget.Target) ?? new LauncherItem
        {
            Id = widget.Target,
            DisplayName = widget.Name ?? NameOf(widget.Target),
            Kind = LauncherItemKind.File,
            Target = widget.Target,
        };

        var tile = new StartTile(item, Math.Round(thickness * 0.5), showLabel: false)
        {
            ToolTip = item.DisplayName,
            Padding = new Thickness(5),
        };
        tile.Clicked += _ => AppLauncher.Launch(item);
        tile.RightClicked += t => service.ShowWidgetMenu(t, widget);
        return tile;
    }

    private static FrameworkElement Widget(BarService service, BarWidget widget, double thickness)
    {
        var view = service.BuildApplet(widget.Target, Math.Round(thickness) - 8);
        if (view is null) throw new InvalidOperationException($"no widget '{widget.Target}'");
        var host = new Border { Child = view, Padding = new Thickness(4, 0, 4, 0) };
        host.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            service.ShowWidgetMenu(host, widget);
        };
        return host;
    }

    public static string NameOf(string target)
    {
        try
        {
            var trimmed = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return name.Length > 0 ? Path.GetFileNameWithoutExtension(name) : trimmed;
        }
        catch (ArgumentException)
        {
            return target;
        }
    }
}

/// <summary>
/// A stack: a folder on the bar that opens into its contents.
///
/// The icon is the folder's first few items arranged behind one another, so a
/// stack looks like the pile it is rather than a generic folder glyph — which
/// is the one thing the Mac got right about them and everyone else copied
/// badly.
/// </summary>
internal sealed class StackButton : PressableBorder
{
    private readonly BarService _service;
    private readonly BarWidget _widget;
    private readonly ScreenEdge _edge;
    private StackFlyout? _open;

    public StackButton(BarService service, BarWidget widget, double thickness, ScreenEdge edge)
        : base(radius: 8)
    {
        _service = service;
        _widget = widget;
        _edge = edge;

        var size = Math.Round(thickness * 0.5);
        Padding = new Thickness(5);
        ToolTip = widget.Name ?? BarWidgetFactory.NameOf(widget.Target);
        Child = BuildPile(size);

        Clicked += _ => Toggle(size);
        RightClicked += b => service.ShowWidgetMenu(b, widget);
    }

    /// <summary>The first few entries, stacked with a slight offset, newest on top.</summary>
    private FrameworkElement BuildPile(double size)
    {
        var host = new Grid { Width = size, Height = size };
        var entries = Entries().Take(3).Reverse().ToList();

        if (entries.Count == 0)
        {
            host.Children.Add(StartStyle.GlyphText(StartStyle.Glyph(0xE8B7), size * 0.8, StartStyle.Muted));
            return host;
        }

        var step = size * 0.13;
        for (var i = 0; i < entries.Count; i++)
        {
            var depth = entries.Count - 1 - i;
            var tile = new StartTile(entries[i], size * (1 - depth * 0.12), showLabel: false)
            {
                RenderTransform = new TranslateTransform(-depth * step, -depth * step),
                Opacity = depth == 0 ? 1 : 0.55,
                IsHitTestVisible = false,
                Padding = new Thickness(0),
            };
            host.Children.Add(tile);
        }
        return host;
    }

    private List<LauncherItem> Entries() => _service.StackContents(_widget);

    private void Toggle(double size)
    {
        if (_open is { IsOpen: true })
        {
            _open.CloseStack();
            _open = null;
            return;
        }
        _open = StackFlyout.Show(_service, _widget, this, _edge, Entries());
    }
}

/// <summary>
/// What a stack opens into. Three shapes, because the right one depends on what
/// is in the folder: a grid for a handful of apps, a fan for a few, a list when
/// the names are what matter.
/// </summary>
internal sealed class StackFlyout : Window
{
    private bool _allowClose;

    public bool IsOpen => IsVisible;

    /// <summary>The button this was opened from, so a click on it is not treated as a click away.</summary>
    public StackButton? Owner { get; private set; }

    public static StackFlyout? Show(BarService service, BarWidget widget, FrameworkElement anchor,
        ScreenEdge edge, List<LauncherItem> items)
    {
        try
        {
            var flyout = new StackFlyout(service, widget, edge, items) { Owner = anchor as StackButton };
            BarFlyout.Present(flyout, anchor, edge, gap: 10);
            service.Register(flyout);
            return flyout;
        }
        catch (Exception ex)
        {
            Log.Error("bar: opening a stack", ex);
            return null;
        }
    }

    private StackFlyout(BarService service, BarWidget widget, ScreenEdge edge, List<LauncherItem> items)
    {
        Title = "Hearth stack";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        // Activated on purpose: a stack opened from a bar that never takes
        // focus would otherwise never be deactivated, and so never close when
        // you clicked away from it.
        ShowActivated = true;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = StartStyle.Body;
        Resources = StartStyle.WindowResources();

        var content = widget.Stack switch
        {
            StackStyle.Fan => Fan(service, items, edge),
            StackStyle.List => List(service, items),
            _ => Grid(service, items),
        };

        var card = new Border
        {
            Background = BarStyleSheet.Surface,
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Child = content,
        };
        // The fan is meant to float free of a card.
        Content = widget.Stack == StackStyle.Fan ? content : card;

        Deactivated += (_, _) => CloseStack();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) CloseStack(); };
        Loaded += (_, _) => Animate();
    }

    private static UIElement Grid(BarService service, List<LauncherItem> items)
    {
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Min(items.Count, 36))));
        var grid = new UniformGrid { Columns = columns };
        foreach (var item in items.Take(36)) grid.Children.Add(Tile(service, item, 44, label: true));
        return grid;
    }

    private static UIElement List(BarService service, List<LauncherItem> items)
    {
        var panel = new StackPanel { MaxWidth = 320 };
        foreach (var item in items.Take(24))
        {
            var row = new ResultRow(item.DisplayName, null, item, iconSize: 22);
            row.Clicked += _ => Launch(service, item);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>
    /// The fan: tiles on an arc rising away from the bar. Each one is its own
    /// little card, so it reads as items lifted off the pile rather than a menu.
    /// </summary>
    private static UIElement Fan(BarService service, List<LauncherItem> items, ScreenEdge edge)
    {
        var shown = items.Take(9).ToList();
        var canvas = new Canvas { Background = Brushes.Transparent };
        const double step = 58;
        const double lean = 16;

        for (var i = 0; i < shown.Count; i++)
        {
            var tile = Tile(service, shown[i], 40, label: false);
            var card = new Border
            {
                Background = BarStyleSheet.Surface,
                BorderBrush = StartStyle.CardEdge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6),
                Child = tile,
                ToolTip = shown[i].DisplayName,
            };
            // Each one a little further out and a little further along, so the
            // run curves instead of stacking straight up.
            var along = lean * Math.Sin(i / (double)Math.Max(1, shown.Count - 1) * Math.PI);
            var out_ = (shown.Count - 1 - i) * step;
            if (edge is ScreenEdge.Left or ScreenEdge.Right)
            {
                Canvas.SetLeft(card, edge == ScreenEdge.Left ? out_ : 0);
                Canvas.SetTop(card, along + i * 4);
            }
            else
            {
                Canvas.SetLeft(card, along + i * 4);
                Canvas.SetTop(card, edge == ScreenEdge.Top ? out_ : 0);
            }
            canvas.Children.Add(card);
        }

        var span = shown.Count * step;
        canvas.Width = edge is ScreenEdge.Left or ScreenEdge.Right ? span + 60 : lean + 60;
        canvas.Height = edge is ScreenEdge.Left or ScreenEdge.Right ? lean + 60 : span + 60;
        return canvas;
    }

    private static FrameworkElement Tile(BarService service, LauncherItem item, double size, bool label)
    {
        var tile = new StartTile(item, size, showLabel: label) { Width = label ? 84 : double.NaN };
        tile.Clicked += _ => Launch(service, item);
        return tile;
    }

    private static void Launch(BarService service, LauncherItem item)
    {
        AppLauncher.Launch(item);
        service.CloseStacks();
    }

    private void Animate()
    {
        var slide = new TranslateTransform();
        if (Content is UIElement element) element.RenderTransform = slide;
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>Guarded: closing deactivates, and deactivation asks to close again.</summary>
    public void CloseStack()
    {
        if (_allowClose) return;
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }
}
