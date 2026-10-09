using System.Windows;
using Hearth.Core.Settings;

namespace Hearth.App.Views.Start;

/// <summary>
/// How much room the Start menu's own furniture takes, so the views inside it
/// can have the rest.
///
/// The menu is a fixed 4:3 box scaled as a whole, which means every DIP the
/// search box, tabs and footer give back goes straight to the Pages card —
/// compact hands it about 50 of 660, a tenth more height for the same grid.
/// Nothing here changes the layout's shape, only its padding and type sizes,
/// so both densities lay out identically at any scale.
///
/// The window is built once and kept, so a change of density rebuilds it
/// (<see cref="Hosting.StartMenuController.InvalidateWindow"/>); the effective
/// value is recorded on the window as <c>Density</c> and compared on every open.
/// </summary>
internal sealed record StartMetrics(
    Thickness SearchMargin,
    Thickness SearchPadding,
    double SearchFont,
    double SearchGlyphLeft,
    double HintLeft,
    Thickness TabsMargin,
    Thickness TabPadding,
    double TabFont,
    double TabRadius,
    double TabGap,
    Thickness ContentMargin,
    Thickness FooterMargin,
    Thickness UserPadding,
    double Avatar,
    double UserFont,
    double FooterGlyph,
    double AppRowWidth,
    double CategoryTileWidth,
    double CategoryTileHeight,
    double CategoryIcon)
{
    public static readonly StartMetrics Standard = new(
        SearchMargin: new Thickness(24, 22, 24, 0),
        SearchPadding: new Thickness(40, 9, 12, 9),
        SearchFont: 14,
        SearchGlyphLeft: 15,
        HintLeft: 43,
        TabsMargin: new Thickness(22, 14, 24, 8),
        TabPadding: new Thickness(14, 5, 14, 6),
        TabFont: 13,
        TabRadius: 14,
        TabGap: 6,
        ContentMargin: new Thickness(24, 4, 24, 8),
        FooterMargin: new Thickness(16, 8, 16, 8),
        UserPadding: new Thickness(8, 5, 12, 5),
        Avatar: 30,
        UserFont: 13.5,
        FooterGlyph: 16,
        AppRowWidth: 240,
        CategoryTileWidth: 136,
        CategoryTileHeight: 118,
        CategoryIcon: 48);

    public static readonly StartMetrics Compact = new(
        SearchMargin: new Thickness(18, 14, 18, 0),
        SearchPadding: new Thickness(33, 5, 10, 5),
        SearchFont: 12.5,
        SearchGlyphLeft: 12,
        HintLeft: 36,
        TabsMargin: new Thickness(16, 8, 18, 4),
        TabPadding: new Thickness(11, 3, 11, 4),
        TabFont: 12,
        TabRadius: 12,
        TabGap: 4,
        ContentMargin: new Thickness(18, 2, 18, 5),
        FooterMargin: new Thickness(12, 4, 12, 4),
        UserPadding: new Thickness(6, 3, 10, 3),
        Avatar: 24,
        UserFont: 12.5,
        FooterGlyph: 14,
        AppRowWidth: 200,
        CategoryTileWidth: 112,
        CategoryTileHeight: 102,
        CategoryIcon: 42);

    /// <summary>
    /// Auto follows the placement: the menu in the middle of the screen is the
    /// one that wants every row it can get, because nothing around it is
    /// giving the grid context, so those placements come up compact.
    /// </summary>
    public static StartDensity Effective(HearthSettings settings) => settings.StartDensity switch
    {
        StartDensity.Standard => StartDensity.Standard,
        StartDensity.Compact => StartDensity.Compact,
        _ => settings.StartPlacement is StartPlacement.Centre or StartPlacement.CentreLarge
            ? StartDensity.Compact
            : StartDensity.Standard,
    };

    public static StartMetrics For(StartDensity density) =>
        density == StartDensity.Compact ? Compact : Standard;

    /// <summary>The page grid the density asks for.</summary>
    public static (int Columns, int Rows) GridFor(StartDensity density) =>
        density == StartDensity.Compact ? StartLayout.CompactGrid : StartLayout.StandardGrid;
}
