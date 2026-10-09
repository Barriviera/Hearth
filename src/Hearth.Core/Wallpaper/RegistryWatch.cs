using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Hearth.Core.Wallpaper;

/// <summary>
/// Calls back whenever anything under a registry key changes.
///
/// <c>RegNotifyChangeKeyValue</c> signals an event once and then has to be
/// asked again, so the wait is re-armed inside the callback. A registry watch
/// is the only signal some background changes produce at all — a slideshow
/// stepping to the next picture broadcasts nothing.
/// </summary>
public sealed class RegistryWatch : IDisposable
{
    private readonly RegistryKey _key;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Action _changed;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    private RegistryWatch(RegistryKey key, Action changed)
    {
        _key = key;
        _changed = changed;
    }

    public static RegistryWatch? TryCreate(RegistryHive hive, string path, Action changed)
    {
        try
        {
            var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            var key = root.OpenSubKey(path);
            if (key is null)
            {
                root.Dispose();
                return null;
            }

            var watch = new RegistryWatch(key, changed);
            if (!watch.Arm())
            {
                watch.Dispose();
                return null;
            }

            watch._wait = ThreadPool.RegisterWaitForSingleObject(watch._signal, (_, _) => watch.OnSignalled(),
                null, Timeout.Infinite, executeOnlyOnce: false);
            return watch;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"[Hearth] registry watch on {path} failed: {ex.Message}");
            return null;
        }
    }

    private bool Arm()
    {
        if (_disposed) return false;
        var handle = _key.Handle;
        if (handle.IsInvalid || handle.IsClosed) return false;
        var filter = REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET;
        return RegNotifyChangeKeyValue(handle, watchSubtree: true, filter, _signal.SafeWaitHandle, asynchronous: true) == 0;
    }

    private void OnSignalled()
    {
        if (_disposed) return;
        Arm();
        try { _changed(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Hearth] registry watch callback failed: {ex}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wait?.Unregister(null);
        _wait = null;
        _key.Dispose();
        _signal.Dispose();
    }

    private const int REG_NOTIFY_CHANGE_NAME = 0x00000001;
    private const int REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle key,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        int notifyFilter,
        SafeWaitHandle eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
