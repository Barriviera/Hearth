using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using Hearth.Core.Diagnostics;
using ComData = System.Runtime.InteropServices.ComTypes.IDataObject;
using ComStream = System.Runtime.InteropServices.ComTypes.IStream;
using DragData = System.Windows.IDataObject;

namespace Hearth.App.Widgets.Shelf;

/// <summary>A file the source of a drag hands over as bytes, not as a path.</summary>
internal sealed record ShelfDropFile(string Name, byte[] Bytes);

/// <summary>
/// What a drag that isn't a file on disk is offering: a browser dragging an
/// image or a link, an app dragging a picture, or some selected text.
/// </summary>
internal sealed class ShelfDropContent
{
    /// <summary>Bytes the source handed over, with the names it gave them.</summary>
    public IReadOnlyList<ShelfDropFile> Files { get; init; } = [];

    /// <summary>The image or page the drag points at, if it named one.</summary>
    public Uri? Link { get; init; }

    public string? Title { get; init; }

    /// <summary>The link is a picture, so fetching it is worth doing.</summary>
    public bool IsImage { get; init; }

    /// <summary>A dragged bitmap, already encoded as PNG. The last resort.</summary>
    public byte[]? Snapshot { get; init; }

    public string? Text { get; init; }

    public bool IsEmpty =>
        Files.Count == 0 && Link is null && Snapshot is null && string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Reads a drag that came from a browser (or anything else that isn't
/// Explorer). Browsers don't put paths on the clipboard for an image on a
/// page: they offer the bytes through the shell's virtual-file formats
/// (FileGroupDescriptorW + FileContents), and the URL through
/// UniformResourceLocatorW or text/x-moz-url.
/// </summary>
internal static class BrowserDrop
{
    private const string DescriptorFormat = "FileGroupDescriptorW";
    private const string ContentsFormat = "FileContents";
    private const string UrlFormat = "UniformResourceLocatorW";
    private const string UrlAnsiFormat = "UniformResourceLocator";
    private const string MozUrlFormat = "text/x-moz-url";

    private const int MaxFiles = 25;
    private const int MaxBytes = 64 * 1024 * 1024;

    // sizeof(FILEDESCRIPTORW), and the offsets of the two fields we read.
    private const int DescriptorSize = 592;
    private const int AttributesOffset = 36;
    private const int NameOffset = 72;
    private const int NameBytes = 520;
    private const uint FileAttributeDirectory = 0x10;

    /// <summary>
    /// Is there anything here worth taking? Cheap enough for DragOver, which
    /// fires many times a second: it only asks which formats exist.
    /// </summary>
    public static bool IsPresent(DragData data) =>
        Present(data, DescriptorFormat) || Present(data, UrlFormat) || Present(data, UrlAnsiFormat) ||
        Present(data, MozUrlFormat) || Present(data, DataFormats.Bitmap) || Present(data, DataFormats.UnicodeText);

    /// <summary>
    /// Takes everything the drag is offering. This has to happen on the
    /// thread handling the drop, while the source still has its data object
    /// alive, so it can block briefly while the browser produces the bytes.
    /// </summary>
    public static ShelfDropContent? Take(DragData data)
    {
        try
        {
            var com = data as ComData;
            var files = VirtualFiles(data, com);
            var (link, title) = LinkOf(data, com);

            var content = new ShelfDropContent
            {
                Files = files,
                Link = link,
                Title = title,
                IsImage = link is not null && LooksLikeImage(data, link),
                Snapshot = files.Count == 0 ? Snapshot(data) : null,
                Text = link is null && files.Count == 0 ? TextOf(data) : null,
            };
            return content.IsEmpty ? null : content;
        }
        catch (Exception ex)
        {
            Log.Error("shelf: reading the drag", ex);
            return null;
        }
    }

    // ---- The shell's virtual files ---------------------------------------

    private static List<ShelfDropFile> VirtualFiles(DragData data, ComData? com)
    {
        var files = new List<ShelfDropFile>();

        // FileContents is an indexed format, so only the COM call can reach it.
        if (com is null) return files;
        if (Raw(data, com, DescriptorFormat, -1) is not { Length: > 4 } blob) return files;

        var contents = unchecked((short)RegisterClipboardFormat(ContentsFormat));
        var count = Math.Min(BitConverter.ToUInt32(blob, 0), MaxFiles);
        for (var i = 0; i < count; i++)
        {
            var start = 4 + (i * DescriptorSize);
            if (start + DescriptorSize > blob.Length) break;

            var attributes = BitConverter.ToUInt32(blob, start + AttributesOffset);
            if ((attributes & FileAttributeDirectory) != 0) continue; // a folder has no contents to write

            var name = Encoding.Unicode.GetString(blob, start + NameOffset, NameBytes).Split('\0')[0];
            if (ComBytes(com, contents, i) is not { Length: > 0 } bytes) continue;
            files.Add(new ShelfDropFile(ShelfSave.SafeName(name, "Dropped file"), bytes));
        }
        return files;
    }

    // ---- What the drag points at -----------------------------------------

    private static (Uri? Link, string? Title) LinkOf(DragData data, ComData? com)
    {
        string? title = null;

        // Firefox: "url\ntitle" in one Unicode block.
        if (Unicode(Raw(data, com, MozUrlFormat, -1)) is { Length: > 0 } moz)
        {
            var parts = moz.Split('\n');
            if (parts.Length > 1) title = parts[1].Trim();
            if (ToUri(parts[0]) is { } fromMoz) return (fromMoz, title);
        }

        if (ToUri(Unicode(Raw(data, com, UrlFormat, -1))) is { } wide) return (wide, title);
        if (ToUri(Ansi(Raw(data, com, UrlAnsiFormat, -1))) is { } ansi) return (ansi, title);
        if (ToUri(ImageSource(HtmlOf(data))) is { } fromHtml) return (fromHtml, title);
        if (ToUri(TextOf(data)) is { } fromText) return (fromText, title);
        return (null, title);
    }

    private static bool LooksLikeImage(DragData data, Uri link)
    {
        if (link.Scheme == "data") return link.OriginalString.StartsWith("data:image", StringComparison.OrdinalIgnoreCase);
        if (ShelfSave.IsPictureExtension(Path.GetExtension(link.AbsolutePath))) return true;
        return HtmlOf(data) is { } html && html.Contains("<img", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ImageSource(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        var match = Regex.Match(html, "<img[^>]+?src\\s*=\\s*[\"']?([^\"'\\s>]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    /// <summary>A dragged picture with no file behind it, encoded once as PNG.</summary>
    private static byte[]? Snapshot(DragData data)
    {
        try
        {
            if (!Present(data, DataFormats.Bitmap)) return null;
            if (data.GetData(DataFormats.Bitmap) is not BitmapSource source) return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return buffer.Length is > 0 and <= MaxBytes ? buffer.ToArray() : null;
        }
        catch (Exception ex)
        {
            Log.Write($"shelf: dragged picture: {ex.Message}");
            return null;
        }
    }

    // ---- Talking to the drag's data object -------------------------------

    private static bool Present(DragData data, string format)
    {
        try
        {
            return data.GetDataPresent(format);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? TextOf(DragData data)
    {
        try
        {
            return Present(data, DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static string? HtmlOf(DragData data)
    {
        try
        {
            return Present(data, DataFormats.Html) ? data.GetData(DataFormats.Html) as string : null;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The raw block for a format, through COM so an index can be asked for.</summary>
    private static byte[]? Raw(DragData data, ComData? com, string format, int index)
    {
        var id = unchecked((short)RegisterClipboardFormat(format));
        if (com is not null && ComBytes(com, id, index) is { Length: > 0 } bytes) return bytes;
        if (index != -1) return null;

        try
        {
            if (Present(data, format) && data.GetData(format) is MemoryStream stream) return stream.ToArray();
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
        {
            Log.Write($"shelf: drag format {format}: {ex.Message}");
        }
        return null;
    }

    private static byte[]? ComBytes(ComData com, short format, int index)
    {
        if (format == 0) return null;

        var request = new FORMATETC
        {
            cfFormat = format,
            ptd = IntPtr.Zero,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM,
        };

        var medium = default(STGMEDIUM);
        try
        {
            com.GetData(ref request, out medium);
            return medium.tymed switch
            {
                TYMED.TYMED_HGLOBAL => FromGlobal(medium.unionmember),
                TYMED.TYMED_ISTREAM => FromStream(medium.unionmember),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is COMException or ExternalException or NotSupportedException
                                       or InvalidCastException or ArgumentException or NotImplementedException)
        {
            // Normal: most sources offer only some of these formats.
            Log.Write($"shelf: drag format {format} [{index}]: {ex.Message}");
            return null;
        }
        finally
        {
            if (medium.unionmember != IntPtr.Zero || medium.pUnkForRelease is not null) ReleaseStgMedium(ref medium);
        }
    }

    private static byte[]? FromGlobal(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;

        var size = (long)(ulong)GlobalSize(handle);
        if (size <= 0) return null;
        if (size > MaxBytes)
        {
            Log.Write($"shelf: dropped data is {size} bytes, too big to keep");
            return null;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static byte[]? FromStream(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;

        ComStream? stream = null;
        var counter = IntPtr.Zero;
        try
        {
            stream = Marshal.GetObjectForIUnknown(handle) as ComStream;
            if (stream is null) return null;

            try
            {
                stream.Seek(0, 0, IntPtr.Zero); // STREAM_SEEK_SET
            }
            catch (Exception ex) when (ex is COMException or NotSupportedException)
            {
                // A one-shot stream: read it from wherever it already is.
            }

            counter = Marshal.AllocCoTaskMem(sizeof(int));
            var chunk = new byte[64 * 1024];
            using var buffer = new MemoryStream();
            while (true)
            {
                stream.Read(chunk, chunk.Length, counter);
                var read = Marshal.ReadInt32(counter);
                if (read <= 0) break;

                buffer.Write(chunk, 0, read);
                if (buffer.Length <= MaxBytes) continue;

                Log.Write("shelf: dropped data is too big to keep");
                return null;
            }
            return buffer.ToArray();
        }
        finally
        {
            if (counter != IntPtr.Zero) Marshal.FreeCoTaskMem(counter);
            if (stream is not null) Marshal.ReleaseComObject(stream);
        }
    }

    private static string? Unicode(byte[]? bytes) =>
        bytes is { Length: > 1 } ? Encoding.Unicode.GetString(bytes).Split('\0')[0] : null;

    private static string? Ansi(byte[]? bytes) =>
        bytes is { Length: > 0 } ? Encoding.Latin1.GetString(bytes).Split('\0')[0] : null;

    private static Uri? ToUri(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > MaxBytes) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "http" or "https" or "data" ? uri : null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);
}
