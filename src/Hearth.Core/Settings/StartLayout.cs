using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hearth.Core.Layout;

namespace Hearth.Core.Settings;

/// <summary>Background material for the Start menu.</summary>
public enum StartBackdrop
{
    /// <summary>
    /// Hearth's own: a blurred snapshot of what is behind the menu. Works even
    /// when Windows has transparency effects off (Energy Saver does that).
    /// </summary>
    Blur,
    /// <summary>Windows 11 Mica: the wallpaper, heavily blurred and tinted.</summary>
    Mica,
    /// <summary>Mica Alt: the same, with a stronger tint.</summary>
    MicaAlt,
    /// <summary>Acrylic: blurs whatever is behind the menu.</summary>
    Acrylic,
}

/// <summary>
/// The Start menu's own arrangement: its pages (apps, folders and widgets on a
/// fixed grid, independent of the desktop once created), the user's app
/// categories, and which widgets the Widgets tab shows.
/// </summary>
public sealed class StartLayout
{
    /// <summary>
    /// How many cells a page has.
    ///
    /// This is what Compact density buys on the Pages view: the card is the
    /// same size, the cells are smaller, and more fits on a page. Standard
    /// stays at the 7x4 every saved layout was built against.
    ///
    /// It is settable rather than constant because one process shows one Start
    /// menu at one density; <see cref="UseGrid"/> is called before the window
    /// is built and re-places anything the new grid no longer holds.
    /// </summary>
    public static int Columns { get; private set; } = StandardGrid.Columns;

    public static int Rows { get; private set; } = StandardGrid.Rows;

    public static readonly (int Columns, int Rows) StandardGrid = (7, 4);

    /// <summary>
    /// Compact: one more column, same rows.
    ///
    /// The width is where the compact chrome actually gives something back, so
    /// that is where the extra cell goes. An extra row as well was tried and
    /// was worse: the cells lost a quarter of their height, which took the
    /// icons down to about 28 px and clipped every two-line label.
    /// </summary>
    public static readonly (int Columns, int Rows) CompactGrid = (8, 4);

    /// <summary>
    /// Changes the grid and puts back anything that fell off it. Returns true
    /// when something moved, so the caller knows to save.
    ///
    /// Growing the grid never moves anything — every old cell is still a cell.
    /// Shrinking it can, so an item hanging over the new edge is lifted and
    /// dropped into the first free run of its own size, its own page first.
    /// </summary>
    public bool UseGrid(int columns, int rows)
    {
        if (columns == Columns && rows == Rows) return false;
        Columns = columns;
        Rows = rows;

        var moved = false;
        var pages = Pages;
        if (pages is null) return false;

        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            page.Columns = Columns;
            page.Rows = Rows;

            var strays = page.Placements
                .Where(p => p.Column + p.ColumnSpan > Columns || p.Row + p.RowSpan > Rows)
                .ToList();
            if (strays.Count == 0) continue;

            foreach (var stray in strays) page.Placements.Remove(stray);
            moved = true;

            // Removed first, then placed: a stray can otherwise be given the
            // cell another stray is still nominally sitting in.
            foreach (var stray in strays)
                Place(stray.ItemId, stray.ColumnSpan, stray.RowSpan, fromPage: index);
        }
        return moved;
    }

    /// <summary>
    /// Pages in order. Null until the first page set is made (copied from the
    /// desktop), so an emptied Start stays empty rather than refilling.
    /// </summary>
    public List<MonitorLayout>? Pages { get; set; }

    /// <summary>Start's own folders; their tiles are placed as "group:{id}".</summary>
    public List<GridGroup> Groups { get; set; } = [];

    public MonitorLayout AddPage()
    {
        Pages ??= [];
        var page = new MonitorLayout { MonitorId = Guid.NewGuid().ToString("N")[..12], Columns = Columns, Rows = Rows };
        Pages.Add(page);
        return page;
    }

    public GridGroup? FindGroup(string placementId) =>
        placementId.StartsWith(HomeLayout.GroupPrefix, StringComparison.Ordinal)
            ? Groups.FirstOrDefault(g => g.Id == placementId[HomeLayout.GroupPrefix.Length..])
            : null;

    /// <summary>Where an item sits, if it is on a page (folders count as their tile).</summary>
    public (MonitorLayout Page, GridPlacement Placement)? Locate(string placementId)
    {
        foreach (var page in Pages ?? [])
        {
            if (page.Find(placementId) is { } placement) return (page, placement);
        }
        return null;
    }

    public bool Contains(string itemId) =>
        Locate(itemId) is not null ||
        Groups.Any(g => g.ItemIds.Contains(itemId, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Puts an item in the first free run of cells, starting at
    /// <paramref name="fromPage"/>, adding a page when every page is full.
    /// </summary>
    public GridPlacement Place(string itemId, int columnSpan = 1, int rowSpan = 1, int fromPage = 0)
    {
        columnSpan = Math.Clamp(columnSpan, 1, Columns);
        rowSpan = Math.Clamp(rowSpan, 1, Rows);
        var pages = Pages ??= [];

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[(fromPage + i) % pages.Count];
            if (page.FindFreeRegion(columnSpan, rowSpan, Columns, Rows) is not { } cell) continue;
            return Add(page, cell);
        }

        return Add(AddPage(), (0, 0));

        GridPlacement Add(MonitorLayout page, (int Column, int Row) cell)
        {
            var placement = new GridPlacement
            {
                ItemId = itemId,
                Column = cell.Column,
                Row = cell.Row,
                ColumnSpan = columnSpan,
                RowSpan = rowSpan,
            };
            page.Placements.Add(placement);
            return placement;
        }
    }

    /// <summary>Takes an item off Start: its tile, and out of any folder (dissolving a folder left with one item).</summary>
    public void Remove(string itemId)
    {
        foreach (var page in Pages ?? []) page.Placements.RemoveAll(p => string.Equals(p.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
        foreach (var group in Groups.ToList())
        {
            if (group.ItemIds.RemoveAll(id => string.Equals(id, itemId, StringComparison.OrdinalIgnoreCase)) > 0) Tidy(group);
        }
        if (FindGroup(itemId) is { } removed) Groups.Remove(removed);
    }

    /// <summary>A folder with one item becomes that item; an empty one goes.</summary>
    public void Tidy(GridGroup group)
    {
        if (group.ItemIds.Count >= 2) return;
        var spot = Locate(group.PlacementId);
        if (group.ItemIds.Count == 1 && spot is { } tile && Locate(group.ItemIds[0]) is null)
            tile.Placement.ItemId = group.ItemIds[0];
        else if (spot is { } empty)
            empty.Page.Placements.Remove(empty.Placement);
        Groups.Remove(group);
    }

    /// <summary>Drops items that no longer exist. Returns true when anything changed.</summary>
    public bool Prune(Func<string, bool> exists)
    {
        var changed = false;
        foreach (var group in Groups.ToList())
        {
            if (group.ItemIds.RemoveAll(id => !exists(id)) > 0)
            {
                changed = true;
                Tidy(group);
            }
        }
        foreach (var page in Pages ?? [])
        {
            var removed = page.Placements.RemoveAll(p =>
                !exists(p.ItemId) && FindGroup(p.ItemId) is null && !p.ItemId.StartsWith("widget:", StringComparison.OrdinalIgnoreCase));
            if (removed > 0) changed = true;
        }
        return changed;
    }

    /// <summary>Item id to the category the user moved it to.</summary>
    public Dictionary<string, string> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Widget ids shown on the Widgets tab, in order. Null means "the defaults".</summary>
    public List<string>? Widgets { get; set; }

    /// <summary>Widgets shown full width on the Widgets board.</summary>
    public List<string> WideWidgets { get; set; } = [];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Hearth", "start.json");

    public static StartLayout Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new StartLayout();
            var layout = JsonSerializer.Deserialize<StartLayout>(File.ReadAllText(path), SerializerOptions) ?? new StartLayout();
            layout.Categories = new Dictionary<string, string>(layout.Categories, StringComparer.OrdinalIgnoreCase);
            return layout;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[Hearth] start layout load failed, starting fresh: {ex.Message}");
            return new StartLayout();
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
}
