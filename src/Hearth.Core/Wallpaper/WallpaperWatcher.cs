using System.IO;
using Microsoft.Win32;

namespace Hearth.Core.Wallpaper;

/// <summary>
/// Tells Hearth when the user's background changes, so the desktop repaints
/// itself instead of waiting for a restart.
///
/// Hearth paints the wallpaper itself (see <see cref="WallpaperService"/>), so
/// nothing about a background change reaches it on its own: Explorer redraws
/// its own desktop, which Hearth is covering. There is also no single reliable
/// signal, so three are used together and the result is debounced:
///
///   - WM_SETTINGCHANGE / SPI_SETDESKWALLPAPER, which the Settings app sends
///     (fed in by the host window, which is the only thing with a message loop);
///   - the registry, which is what a slideshow and "Set as desktop background"
///     both rewrite, and which arrives even when no broadcast does;
///   - the themes folder, where Windows keeps the transcoded copy it draws.
///
/// The callback fires on a background thread and may fire more than once for a
/// single change; the caller compares <see cref="WallpaperService.Signature"/>
/// before doing any work.
/// </summary>
public sealed class WallpaperWatcher : IDisposable
{
    /// <summary>SystemParametersInfo action for a background change, as WM_SETTINGCHANGE's wParam.</summary>
    public const int SPI_SETDESKWALLPAPER = 0x0014;

    private const int WM_SETTINGCHANGE = 0x001A;

    private readonly Action _changed;
    private readonly Timer _debounce;
    private readonly List<RegistryWatch> _registry = [];
    private readonly List<FileSystemWatcher> _files = [];
    private bool _disposed;

    public WallpaperWatcher(Action changed)
    {
        _changed = changed;
        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

        // "WallPaper" and the fit mode live here; a slideshow rewrites them on
        // every step.
        Watch(RegistryHive.CurrentUser, @"Control Panel\Desktop");
        // Per-monitor wallpapers (IDesktopWallpaper) are kept here instead.
        Watch(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers");

        var themes = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes");
        WatchFolder(themes);
        WatchFolder(Path.Combine(themes, "CachedFiles"));
    }

    /// <summary>Fed from the host window's WndProc. Returns true when the message was a background change.</summary>
    public bool OnWindowMessage(int message, IntPtr wParam)
    {
        if (message != WM_SETTINGCHANGE || (int)wParam != SPI_SETDESKWALLPAPER) return false;
        Schedule();
        return true;
    }

    /// <summary>Asks for a check even without a signal (a display change, coming back from sleep).</summary>
    public void Poke() => Schedule();

    /// <summary>
    /// A watch on one HKCU key, for anything else that needs to hear about a
    /// registry change. The mechanics are the same and there is no reason for a
    /// second copy of them.
    /// </summary>
    public static IDisposable? WatchKey(string path, Action changed) =>
        RegistryWatch.TryCreate(Microsoft.Win32.RegistryHive.CurrentUser, path, changed);

    private void Watch(RegistryHive hive, string path)
    {
        var watch = RegistryWatch.TryCreate(hive, path, Schedule);
        if (watch is not null) _registry.Add(watch);
    }

    private void WatchFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            watcher.Changed += (_, _) => Schedule();
            watcher.Created += (_, _) => Schedule();
            watcher.Renamed += (_, _) => Schedule();
            _files.Add(watcher);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[Hearth] wallpaper folder watch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A change arrives as several writes in a row (the path, then the fit
    /// mode, then the transcoded copy), and the transcoded file is still being
    /// written when the first of them lands.
    /// </summary>
    private void Schedule()
    {
        if (_disposed) return;
        try { _debounce.Change(350, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void Fire()
    {
        if (_disposed) return;
        try { _changed(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Hearth] wallpaper change handler failed: {ex}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var watch in _registry) watch.Dispose();
        _registry.Clear();
        foreach (var watcher in _files) watcher.Dispose();
        _files.Clear();
        _debounce.Dispose();
    }
}
