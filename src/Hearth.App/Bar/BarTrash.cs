using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;
using Hearth.Core.Shell;

namespace Hearth.App.Bar;

/// <summary>
/// The recycle bin on the bar.
///
/// The one thing a dock has always had at its end, and the reason is that it is
/// the only place a drag can end up that is not a folder. It shows whether it
/// has anything in it — full and empty are different icons, and a bin that
/// looks the same either way tells you nothing — and it takes a drop.
/// </summary>
internal sealed class BarTrash : PressableBorder
{
    private readonly Image _icon = new();
    private readonly DispatcherTimer _poll;
    private bool _full;

    public BarTrash(BarService service, double size) : base(radius: 8)
    {
        _icon.Width = size;
        _icon.Height = size;
        _icon.Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);

        Padding = new Thickness(5);
        Child = _icon;
        AllowDrop = true;

        Clicked += _ => ShellLauncher.Open("shell:RecycleBinFolder");
        RightClicked += _ => ShowMenu(service);

        Drop += (_, e) => OnDrop(e);
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };

        // Cheap enough to ask for often; the answer changes without any event
        // Hearth could hook.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _poll.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _poll.Start(); };
        Unloaded += (_, _) => _poll.Stop();
    }

    private void Refresh()
    {
        var full = HasContents();
        if (full == _full && _icon.Source is not null) return;
        _full = full;
        ToolTip = full ? "Recycle Bin — not empty" : "Recycle Bin — empty";

        try
        {
            var icon = IconExtractorFor(full);
            if (icon is not null) _icon.Source = icon;
        }
        catch (Exception ex)
        {
            Log.Write($"bar: recycle bin icon: {ex.Message}");
        }
    }

    /// <summary>The shell's own bin icons, so it looks like the one on the desktop.</summary>
    private static ImageSource? IconExtractorFor(bool full) =>
        Hearth.Core.Icons.IconExtractor.Extract("shell:RecycleBinFolder", 128)?.ToBitmapSource() is { } source
            ? source
            : null;

    private static bool HasContents()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 && info.i64NumItems > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private void OnDrop(DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        try
        {
            Recycle(paths);
            Refresh();
        }
        catch (Exception ex)
        {
            Log.Error("bar: deleting a drop", ex);
        }
    }

    private void ShowMenu(BarService service)
    {
        var menu = StartStyle.NewMenu(this);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

        var empty = new MenuItem { Header = "Empty the Recycle Bin", IsEnabled = _full };
        empty.Click += (_, _) =>
        {
            try
            {
                // The shell's own confirmation and progress; Hearth does not
                // get to decide that deleting everything needs no warning.
                SHEmptyRecycleBin(IntPtr.Zero, null, 0);
                Refresh();
            }
            catch (Exception ex)
            {
                Log.Error("bar: emptying the recycle bin", ex);
            }
        };
        menu.Items.Add(empty);

        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => ShellLauncher.Open("shell:RecycleBinFolder");
        menu.Items.Add(open);
        menu.Items.Add(new Separator());

        var remove = new MenuItem { Header = "Remove from the bar" };
        remove.Click += (_, _) =>
        {
            service.Settings.Widgets.RemoveAll(w => w.Kind == BarWidgetKind.RecycleBin);
            service.SaveAndApply();
        };
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Sends files to the Recycle Bin, with the shell's own progress and undo.
    /// FOF_ALLOWUNDO is what makes it the bin rather than a deletion, and the
    /// user's own "ask me first" setting is respected because the shell asks,
    /// not Hearth.
    /// </summary>
    private static void Recycle(string[] paths)
    {
        // Built at run time: a NUL written into the source becomes a real
        // NUL byte in the file, which the build then chokes on.
        var nul = ((char)0).ToString();
        var from = string.Join(nul, paths) + nul + nul;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = FOF_ALLOWUNDO,
        };
        var result = SHFileOperation(ref op);
        if (result != 0) Log.Write($"bar: the shell refused the delete (0x{result:X})");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x0040;

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr owner, string? rootPath, uint flags);
}
