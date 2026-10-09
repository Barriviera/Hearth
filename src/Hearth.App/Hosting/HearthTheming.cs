using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;
using Hearth.Core.Wallpaper;
using Hearth.App.Views.Start;

namespace Hearth.App.Hosting;

/// <summary>
/// Keeps Hearth's light/dark choice applied everywhere, and follows Windows'
/// when the choice is Auto.
///
/// The palette is settled once and read by everything, so changing it means
/// rebuilding the things that captured it: the bar's windows, the Start menu,
/// and any dialog that is open. Nothing here redraws the desktop's icons —
/// their theme is a separate, cached decision, and re-rendering two hundred of
/// them for a colour change nobody asked about would be the slowest thing in
/// the app.
/// </summary>
internal static class HearthTheming
{
    private static RegistryThemeWatch? _watch;
    private static bool _applied;

    /// <summary>Settles the palette and starts following Windows for Auto.</summary>
    public static void Start()
    {
        StartStyle.UseSystemTheme();
        _applied = StartStyle.IsLight;
        _watch ??= RegistryThemeWatch.Create(OnSystemThemeChanged);
    }

    /// <summary>Called after the setting changes: re-settles and rebuilds what shows it.</summary>
    public static void Reapply()
    {
        StartStyle.UseSystemTheme();
        _applied = StartStyle.IsLight;
        Rebuild();
    }

    private static void OnSystemThemeChanged()
    {
        // Only Auto follows Windows, and only a real change is worth a rebuild:
        // the Personalize key is written for things Hearth does not care about.
        if (App.Settings.Theme != HearthTheme.Auto) return;
        if (SystemTheme.SystemUsesLight == _applied) return;
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (App.Settings.Theme != HearthTheme.Auto) return;
            if (SystemTheme.SystemUsesLight == _applied) return;
            Log.Write($"theme: Windows switched to {(SystemTheme.SystemUsesLight ? "light" : "dark")}; following");
            Reapply();
        });
    }

    private static void Rebuild()
    {
        try
        {
            StartMenuController.Current?.InvalidateWindow();
            // The bars captured the old brushes when they were built.
            if (Bar.BarService.Current is { IsRunning: true } bar) bar.SaveAndApply();
        }
        catch (Exception ex)
        {
            Log.Error("theme change", ex);
        }
    }

    public static void Stop()
    {
        _watch?.Dispose();
        _watch = null;
    }
}

/// <summary>Watches the key Windows writes its light/dark choice to.</summary>
internal sealed class RegistryThemeWatch : IDisposable
{
    private readonly IDisposable _watch;

    private RegistryThemeWatch(IDisposable watch) => _watch = watch;

    public static RegistryThemeWatch? Create(Action changed)
    {
        var watch = WallpaperWatcher.WatchKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", changed);
        return watch is null ? null : new RegistryThemeWatch(watch);
    }

    public void Dispose() => _watch.Dispose();
}
