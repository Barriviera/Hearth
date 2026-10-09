using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace Hearth.Core.Settings;

/// <summary>
/// The shape Hearth's own bar takes. Each one is a different idea about what a
/// bar is for, not a skin of the same thing.
/// </summary>
public enum BarStyle
{
    /// <summary>Windows' idea: one labelled button per window, a tray and a clock on one strip.</summary>
    Taskbar,

    /// <summary>A Linux panel: a menu button, a window list, applets (Hearth widgets) and a clock.</summary>
    Panel,

    /// <summary>The Mac arrangement: a thin menu bar along the top and a separate, centred dock.</summary>
    MenuBarAndDock,

    /// <summary>One square per application rather than per window, grouped, with a dot for each window.</summary>
    Docklike,

    /// <summary>
    /// Hearth's own: the screen's border is the bar. See <c>Bar/Halo.cs</c>.
    /// </summary>
    Halo,
}

/// <summary>What a bar widget is.</summary>
public enum BarWidgetKind
{
    /// <summary>A folder, opened as a fan or a grid of its contents.</summary>
    Stack,

    /// <summary>Everything on the desktop, as one stack.</summary>
    DesktopStack,

    /// <summary>The recycle bin, with its own icon and a menu.</summary>
    RecycleBin,

    /// <summary>A launcher for one program, path or URL.</summary>
    Shortcut,

    /// <summary>Blank space, for pushing the sections apart.</summary>
    Spacer,

    /// <summary>One of Hearth's ordinary widgets, shrunk into the bar.</summary>
    Widget,
}

/// <summary>How a stack opens.</summary>
public enum StackStyle
{
    /// <summary>A grid of tiles above the icon, like a Windows jump list crossed with a folder.</summary>
    Grid,

    /// <summary>An arc of tiles curving away from the bar, the way a Mac stack fans.</summary>
    Fan,

    /// <summary>A plain list with names, for folders with long file names.</summary>
    List,
}

/// <summary>
/// One thing on a bar that is not an application button: a stack, a shortcut,
/// a spacer, a widget.
///
/// This is what makes the bars more than window lists. A dock without stacks is
/// not a dock, a panel without applets is not a panel, and the old Windows
/// taskbar's folder toolbars are the feature people still miss. One list of
/// these, saved per bar, covers all of it.
/// </summary>
public sealed class BarWidget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public BarWidgetKind Kind { get; set; } = BarWidgetKind.Stack;

    /// <summary>A folder for a stack, a target for a shortcut, a widget id for a widget.</summary>
    public string Target { get; set; } = string.Empty;

    public string? Name { get; set; }

    public StackStyle Stack { get; set; } = StackStyle.Grid;

    /// <summary>Which section it belongs to, so the separators fall in the right places.</summary>
    public BarSection Section { get; set; } = BarSection.End;

    /// <summary>Spacer only: how wide, in DIPs. Zero means "take whatever is left".</summary>
    public double Size { get; set; }
}

/// <summary>The runs a bar divides into, in order along it.</summary>
public enum BarSection
{
    /// <summary>Before the pinned apps.</summary>
    Start,

    /// <summary>Between the pinned apps and what is running.</summary>
    Middle,

    /// <summary>After the running apps, before the tray.</summary>
    End,
}

/// <summary>The shape a floating bar (the dock, mainly) is cut to.</summary>
public enum DockShape
{
    /// <summary>Fully rounded ends — the shape a dock has had since 2001.</summary>
    Pill,

    /// <summary>Softly rounded corners, like a card.</summary>
    Rounded,

    /// <summary>Square. Reads as a panel that happens to float.</summary>
    Square,

    /// <summary>Barely rounded, a hair off square.</summary>
    Subtle,
}

/// <summary>When an auto-hiding bar gets out of the way.</summary>
public enum HideStyle
{
    /// <summary>Always away until the pointer reaches its edge.</summary>
    Always,

    /// <summary>
    /// Only when a window would actually cover it — the behaviour people mean
    /// by "intellihide". An empty desktop keeps its bar.
    /// </summary>
    WhenCovered,
}

/// <summary>A corner of a display.</summary>
public enum Corner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// How much of the ring's pill is on screen when nothing is asking for it.
/// The ring's whole claim is that it costs nothing, so the one piece of it that
/// does occupy space should be able to get out of the way.
/// </summary>
public enum PillSize
{
    /// <summary>Always there in full.</summary>
    Full,

    /// <summary>A dot in the corner until the pointer comes near, then the whole pill.</summary>
    Shrink,

    /// <summary>Nothing at all until the pointer reaches the corner.</summary>
    Hidden,
}

/// <summary>What the bar does about the Windows taskbar while it is running.</summary>
public enum ShellTaskbarHandling
{
    /// <summary>Leave it alone; two bars on screen.</summary>
    Leave,

    /// <summary>
    /// Make it invisible and click-through while leaving it fully alive, so
    /// the hidden-icon flyout and every tray menu still work.
    /// </summary>
    Hide,
}

/// <summary>
/// Hearth's optional bar. Nothing here is needed to run Hearth; the bar is off
/// until it is switched on, and the Windows taskbar is untouched until then.
/// </summary>
public sealed class BarSettings
{
    public bool Enabled { get; set; }

    public BarStyle Style { get; set; } = BarStyle.Taskbar;

    public ScreenEdge Edge { get; set; } = ScreenEdge.Bottom;

    /// <summary>Thickness in DIPs. Clamped per style.</summary>
    public double Thickness { get; set; } = 48;

    public ShellTaskbarHandling ShellTaskbar { get; set; } = ShellTaskbarHandling.Hide;

    /// <summary>A bar on every display, rather than the primary one only.</summary>
    public bool AllDisplays { get; set; } = true;

    /// <summary>Show only the windows that are on this bar's own display.</summary>
    public bool WindowsOfThisDisplayOnly { get; set; } = true;

    public bool ShowStart { get; set; } = true;
    public bool ShowSearch { get; set; }
    public bool ShowTray { get; set; } = true;
    public bool ShowClock { get; set; } = true;
    public bool ShowShowDesktop { get; set; } = true;

    /// <summary>Taskbar and Panel: a title beside each button.</summary>
    public bool ShowTitles { get; set; } = true;

    /// <summary>Taskbar and Panel: one button per application rather than per window.</summary>
    public bool GroupWindows { get; set; }

    /// <summary>Apps kept on the bar whether or not they are running (app ids or paths).</summary>
    public List<string> Pinned { get; set; } = [];

    /// <summary>Panel only: widget ids shown as applets, in order.</summary>
    public List<string> Applets { get; set; } = [];

    /// <summary>
    /// The bar's own things: stacks, shortcuts, spacers and widgets, in the
    /// sections they belong to. This is what makes a dock a dock rather than a
    /// window list.
    /// </summary>
    public List<BarWidget> Widgets { get; set; } = [];

    /// <summary>A line between the pinned apps and what is running.</summary>
    public bool ShowSeparators { get; set; } = true;

    /// <summary>Blur whatever is behind the bar, rather than a flat surface.</summary>
    public bool Blur { get; set; }

    /// <summary>How opaque the bar's own surface is over that blur. 0..1.</summary>
    public double Opacity { get; set; } = 0.95;

    /// <summary>Corner radius for the floating pieces (the dock, the ring's pill).</summary>
    public double CornerRadius { get; set; } = -1;

    /// <summary>The shape a floating bar is cut to.</summary>
    public DockShape Shape { get; set; } = DockShape.Pill;

    /// <summary>Whether a hiding bar stays away or only ducks under a window that would cover it.</summary>
    public HideStyle Hiding { get; set; } = HideStyle.Always;

    /// <summary>How long the slide in and out takes, in ms. Zero is instant.</summary>
    public int HideAnimationMs { get; set; } = 180;

    /// <summary>The corner radius a floating bar of this thickness should have.</summary>
    public CornerRadius DockCorners(double thickness)
    {
        if (CornerRadius >= 0) return new CornerRadius(CornerRadius);
        return Shape switch
        {
            DockShape.Rounded => new CornerRadius(Math.Min(18, thickness / 3)),
            DockShape.Square => new CornerRadius(0),
            DockShape.Subtle => new CornerRadius(4),
            _ => new CornerRadius(thickness / 2),
        };
    }

    // ---- Dock ------------------------------------------------------------

    /// <summary>Menu bar and dock: how much the icon under the pointer grows.</summary>
    public double DockMagnification { get; set; } = 1.45;

    /// <summary>The dock slides away until the pointer reaches its edge.</summary>
    public bool DockAutoHide { get; set; }

    /// <summary>The menu bar does the same, on its own.</summary>
    public bool MenuBarAutoHide { get; set; }

    /// <summary>Taskbar, Panel and Docklike: the one strip they have.</summary>
    public bool AutoHide { get; set; }

    /// <summary>How long the pointer has to rest at the edge before a hidden bar comes back, in ms.</summary>
    public int AutoHideDelay { get; set; } = 120;

    // ---- Halo ------------------------------------------------------------

    /// <summary>Halo: how thick the ring is, in DIPs.</summary>
    public double HaloThickness { get; set; } = 5;

    /// <summary>Halo: how thick the arc under the pointer, and the foreground app's, become.</summary>
    public double HaloActiveThickness { get; set; } = 11;

    /// <summary>Halo: give an app with several windows a proportionally longer arc.</summary>
    public bool HaloWeighByWindows { get; set; } = true;

    /// <summary>Halo: which edges carry the ring. Empty means all four.</summary>
    public List<ScreenEdge> HaloEdges { get; set; } = [];

    /// <summary>Halo: which corner the pill sits in.</summary>
    public Corner PillCorner { get; set; } = Corner.BottomRight;

    /// <summary>Halo: how the pill behaves when the pointer is elsewhere.</summary>
    public PillSize Pill { get; set; } = PillSize.Full;

    /// <summary>Halo: the pill's own scale, so it can be made small without shrinking.</summary>
    public double PillScale { get; set; } = 1.0;

    /// <summary>Halo: keep the pill clear of the screen edge by this much, in DIPs.</summary>
    public double PillMargin { get; set; } = 10;

    // ---- Persistence -----------------------------------------------------

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hearth", "bar.json");

    public static BarSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new BarSettings();
            return JsonSerializer.Deserialize<BarSettings>(File.ReadAllText(path), SerializerOptions) ?? new BarSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[Hearth] bar settings load failed, using defaults: {ex.Message}");
            return new BarSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, SerializerOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The sensible thickness range for a style, so a Halo ring cannot be set to 48 px.</summary>
    public (double Min, double Max, double Default) ThicknessRange() => Style switch
    {
        BarStyle.MenuBarAndDock => (28, 44, 32),
        BarStyle.Docklike => (36, 72, 48),
        BarStyle.Panel => (28, 64, 36),
        BarStyle.Halo => (2, 16, 5),
        _ => (32, 80, 48),
    };
}
