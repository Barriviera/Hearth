using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using Hearth.Core.Diagnostics;
using Hearth.Core.Icons;
using Hearth.Core.Shell;

namespace Hearth.App.Bar;

/// <summary>
/// Puts bar buttons through Hearth's own icon pipeline instead of showing the
/// raw window icon.
///
/// A window icon is whatever the program handed the shell — usually 16 or 32
/// pixels, in whatever shape and padding its author felt like. Next to a home
/// screen where every icon has been trimmed, masked to one silhouette and
/// given a background, a row of those looks like a different program's bar. So
/// the bar asks for the same thing the desktop does: same shape, same size,
/// same adaptive background, same cache.
///
/// The window icon stays as the fallback, and is shown immediately while the
/// real one is fetched, because the pipeline goes to disk and the bar must not
/// wait on it.
/// </summary>
internal static class BarIcons
{
    /// <summary>Fills <paramref name="image"/> with the Hearth-rendered icon for a program.</summary>
    public static async void Apply(Image image, BarAppGroup group, double size, double scale)
    {
        try
        {
            var item = ItemFor(group);
            if (item is null) return;

            var options = App.Settings.ToRenderOptions(scale <= 0 ? 1 : scale) with { Size = size };
            var rendered = await App.Icons.GetAsync(item, options).ConfigureAwait(true);
            // The bar may have been rebuilt out from under us while that ran.
            if (rendered is not null && image.IsLoaded) image.Source = rendered;
        }
        catch (Exception ex)
        {
            Log.Write($"bar: icon for '{group.Program}': {ex.Message}");
        }
    }

    /// <summary>
    /// What to ask the pipeline for. The installed-app entry is preferred — a
    /// packaged app's real icon lives with the package, not with whatever exe
    /// happens to host its window — and the program file is the fallback.
    /// </summary>
    private static LauncherItem? ItemFor(BarAppGroup group)
    {
        var path = group.ProgramPath;
        if (path.Length == 0) return null;

        var known = App.Apps.Current.FirstOrDefault(a =>
            string.Equals(a.Target, path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.FileSystemPath, path, StringComparison.OrdinalIgnoreCase));
        if (known is not null) return known;

        if (!File.Exists(path)) return null;
        return new LauncherItem
        {
            Id = path,
            DisplayName = group.Program,
            Kind = LauncherItemKind.File,
            Target = path,
            FileSystemPath = path,
        };
    }

    /// <summary>An image that shows the window's own icon at once and the rendered one when it arrives.</summary>
    public static Image Build(BarAppGroup group, double size, double scale)
    {
        var image = new Image
        {
            Source = group.Icon,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.Loaded += (_, _) => Apply(image, group, size, scale);
        return image;
    }
}
