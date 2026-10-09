using System.IO;
using System.Net.Http;
using System.Text;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets.Shelf;

/// <summary>
/// Puts what a browser handed over onto the shelf: the bytes it gave us, the
/// picture behind a link it only pointed at, or a shortcut to the page.
/// Runs off the UI thread and outlives the view that started it, so a drop
/// still lands if the desktop relays out while it's saving.
/// </summary>
internal static class ShelfSave
{
    private const int MaxBytes = 64 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();
    private static readonly string[] PictureExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".bmp", ".svg", ".ico", ".tif", ".tiff", ".heic"];

    private static int _running;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Hearth/0.1");
        return client;
    }

    /// <summary>Starts saving; returns at once. The file shows up when it lands.</summary>
    public static void Start(ShelfDropContent content, string folder) => _ = RunAsync(content, folder);

    public static bool IsPictureExtension(string? extension) =>
        extension is { Length: > 1 } && PictureExtensions.Contains(extension.ToLowerInvariant());

    private static async Task RunAsync(ShelfDropContent content, string folder)
    {
        Interlocked.Increment(ref _running);
        ShelfFolder.Report("Saving…");

        string? failure = null;
        try
        {
            if (!await SaveAsync(content, folder).ConfigureAwait(false)) failure = "Nothing to save there";
        }
        catch (Exception ex)
        {
            Log.Error("shelf: saving a drop", ex);
            failure = "Couldn't save that";
        }

        ShelfFolder.NotifyChanged();
        if (Interlocked.Decrement(ref _running) != 0) return;

        if (failure is null)
        {
            ShelfFolder.Report(null);
            return;
        }

        ShelfFolder.Report(failure);
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        if (Volatile.Read(ref _running) == 0) ShelfFolder.Report(null);
    }

    private static async Task<bool> SaveAsync(ShelfDropContent content, string folder)
    {
        Directory.CreateDirectory(folder);

        if (content.Files.Count > 0)
        {
            foreach (var file in content.Files)
                await File.WriteAllBytesAsync(Unique(folder, file.Name), file.Bytes).ConfigureAwait(false);
            return true;
        }

        if (content.Link is { } link)
        {
            if (link.Scheme == "data") return SaveDataUri(link, folder, content.Title);
            if (content.IsImage && await DownloadAsync(link, folder, content.Title).ConfigureAwait(false)) return true;
            if (content.Snapshot is { } fallback) return await SavePictureAsync(fallback, folder, content.Title).ConfigureAwait(false);
            return SaveShortcut(link, folder, content.Title);
        }

        if (content.Snapshot is { } picture) return await SavePictureAsync(picture, folder, content.Title).ConfigureAwait(false);
        if (content.Text is { Length: > 0 } text) return await SaveTextAsync(text, folder).ConfigureAwait(false);
        return false;
    }

    // ---- The ways something can land on the shelf ------------------------

    private static async Task<bool> DownloadAsync(Uri link, string folder, string? title)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var temp = Path.Combine(Path.GetTempPath(), $"hearth-shelf-{Guid.NewGuid():N}.tmp");
        try
        {
            using var response = await Http
                .GetAsync(link, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"shelf: {link.Host} answered {(int)response.StatusCode} for {link.AbsolutePath}");
                return false;
            }

            var type = response.Content.Headers.ContentType?.MediaType;

            // A page, not a picture: a shortcut is the honest thing to keep.
            if (type is not null && type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) return false;

            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                Log.Write($"shelf: {link} is larger than 64 MB, not saved");
                return false;
            }

            await using (var file = File.Create(temp))
            await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                await CopyAsync(stream, file, timeout.Token).ConfigureAwait(false);
            }

            File.Move(temp, Unique(folder, NameFor(link, type, title)));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       or IOException or UnauthorizedAccessException or UriFormatException)
        {
            Log.Write($"shelf: couldn't download {link}: {ex.Message}");
            return false;
        }
        finally
        {
            // A half-finished download, never a file of the user's.
            if (File.Exists(temp)) File.Delete(temp); // check-widgets: allow - a temporary download, not a user file
        }
    }

    private static async Task CopyAsync(Stream from, Stream to, CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await from.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return;

            total += read;
            if (total > MaxBytes) throw new IOException("the download is larger than 64 MB");
            await to.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }

    private static bool SaveDataUri(Uri link, string folder, string? title)
    {
        var value = link.OriginalString;
        var comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma < 5 || value.Length > MaxBytes) return false;

        var header = value[5..comma];
        var payload = value[(comma + 1)..];
        try
        {
            var bytes = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            if (bytes.Length == 0) return false;

            var name = WithExtension(SafeName(title, "Picture"), ExtensionFor(header.Split(';')[0]) ?? ".bin");
            File.WriteAllBytes(Unique(folder, name), bytes);
            return true;
        }
        catch (FormatException ex)
        {
            Log.Write($"shelf: that data: URL didn't decode: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> SavePictureAsync(byte[] png, string folder, string? title)
    {
        var name = WithExtension(SafeName(title, "Picture"), ".png");
        await File.WriteAllBytesAsync(Unique(folder, name), png).ConfigureAwait(false);
        return true;
    }

    private static bool SaveShortcut(Uri link, string folder, string? title)
    {
        var name = WithExtension(SafeName(title, link.Host is { Length: > 0 } host ? host : "Link"), ".url");
        // Explorer reads these as plain ASCII lines; no BOM.
        File.WriteAllText(Unique(folder, name), $"[InternetShortcut]\r\nURL={link.AbsoluteUri}\r\n");
        return true;
    }

    private static async Task<bool> SaveTextAsync(string text, string folder)
    {
        var first = text.Trim().Split('\n')[0].Trim();
        var name = WithExtension(SafeName(first.Length > 40 ? first[..40] : first, "Note"), ".txt");
        await File.WriteAllTextAsync(Unique(folder, name), text).ConfigureAwait(false);
        return true;
    }

    // ---- Names -----------------------------------------------------------

    /// <summary>A name Windows will accept, from whatever the source called it.</summary>
    public static string SafeName(string? name, string fallback)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length > 0) name = Path.GetFileName(name.Replace('/', '\\').TrimEnd('\\'));
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        name = name.Trim(' ', '.');
        if (name.Length == 0) return fallback;
        if (name.Length <= 120) return name;

        var extension = Path.GetExtension(name);
        if (extension.Length > 12) extension = string.Empty;
        return name[..(120 - extension.Length)] + extension;
    }

    private static string NameFor(Uri link, string? contentType, string? title)
    {
        var fromPath = SafeName(Decode(Path.GetFileName(link.AbsolutePath.TrimEnd('/'))), string.Empty);
        var name = fromPath.Length > 0
            ? fromPath
            : SafeName(title, link.Host is { Length: > 0 } host ? host : "Download");

        // A URL ending in .php or nothing at all: let the server's type name it.
        if (ExtensionFor(contentType) is not { } mapped) return name;
        return IsPictureExtension(Path.GetExtension(name)) ? name : WithExtension(name, mapped);
    }

    private static string WithExtension(string name, string extension) =>
        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? name
            : Path.GetFileNameWithoutExtension(name) is { Length: > 0 } stem
                ? stem + extension
                : name + extension;

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string? ExtensionFor(string? contentType) => contentType?.Trim().ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
        "image/png" or "image/apng" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/avif" => ".avif",
        "image/bmp" or "image/x-ms-bmp" => ".bmp",
        "image/svg+xml" => ".svg",
        "image/tiff" => ".tif",
        "image/heic" => ".heic",
        "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
        "application/pdf" => ".pdf",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "audio/mpeg" => ".mp3",
        "text/plain" => ".txt",
        _ => null,
    };

    /// <summary>A path nothing is using yet: "picture (2).png" the second time.</summary>
    private static string Unique(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        if (stem.Length == 0) stem = "Dropped";

        var path = Path.Combine(folder, stem + extension);
        for (var n = 2; n < 1000 && (File.Exists(path) || Directory.Exists(path)); n++)
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        return path;
    }
}
