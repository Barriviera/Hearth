# Shelf (`shelf`)

A place to drop things. Files dropped from Explorer are copied (hold Shift to
move them). Images, links and text dragged **from a browser** are saved as
files too. Drag anything back out to wherever it's going, or click to open.
It holds either Hearth's own stash or a real folder such as Downloads.

## Files

| File | What it is |
| --- | --- |
| `ShelfWidget.cs` | The widget (`IConfigurableWidget`), its view, and drop handling |
| `ShelfFolder.cs` | Settings, listing, adding, and watching the folder |
| `BrowserDrop.cs` | Reads a drag that isn't files on disk: a browser's bytes, link or picture |
| `ShelfSave.cs` | Writes that drop to the folder: bytes, a download, or a `.url` |
| `ShelfSetupWindow.cs` | The settings dialog: stash or folder |
| `ShellCopy.cs` | `SHFileOperation` for copying and moving |

## Data

- Settings: `%AppData%/Hearth/shelf.json`.
- The stash: `%LocalAppData%/Hearth/Shelf/`.

## Dropping from a browser

A browser dragging an image off a page never offers a path, so
`DataFormats.FileDrop` is empty and the drop used to be refused. It offers,
in the order the Shelf prefers them:

1. **The bytes themselves**, through the shell's virtual-file formats
   (`FileGroupDescriptorW` for the names, `FileContents` for the data). This
   is what Explorer uses, and it gives the original file with its own name.
   `FileContents` is an *indexed* format, which `IDataObject.GetData(string)`
   can't ask for, so `BrowserDrop` goes through the COM
   `IDataObject` (WPF's `DataObject` implements it) with a `FORMATETC` whose
   `lindex` is the file's index, accepting `TYMED_HGLOBAL` or `TYMED_ISTREAM`.
2. **A link** — `UniformResourceLocatorW`, `text/x-moz-url` (Firefox), or the
   `src` of the first `<img>` in the `HTML Format` fragment. If it looks like
   a picture, Hearth downloads it; if the answer is a web page, or it isn't a
   picture, an Explorer-style `.url` shortcut is written instead.
3. **A bitmap** (`CF_DIB`), encoded once as PNG. The last resort, for apps
   that drag a picture and nothing else.
4. **Selected text**, saved as a `.txt` named after its first line.

`data:` URLs are decoded rather than fetched. Names are sanitised, and a
clash becomes `picture (2).png`.

### What leaves the machine

Only case 2, and only when the browser handed over a link but no bytes: one
`GET` to that URL, with `Hearth/0.1` as the user agent and nothing else
attached. Sites that block hotlinking will fail that fetch, and a `.url`
shortcut is kept instead.

## Background

- While any Shelf is shown, a `FileSystemWatcher` on the folder (ref-counted,
  debounced 400 ms).
- Copies run on their own STA thread (the shell's file operation blocks).
- Saving a browser drop runs on the thread pool through `ShelfSave`, which is
  static on purpose: the desktop rebuilds views often, and a download must
  not die with the view that started it. Its progress shows in the drop hint
  ("Saving…"), through `ShelfFolder.Status`.
- One static `HttpClient`, 60 s timeout, 64 MB ceiling on anything saved.

## Platform

The shell's file operation, so Explorer drops get its progress window,
"Copy of ..." names on a clash, and undo. Remove and Delete use the Recycle
Bin (`ShellLauncher.Recycle`). Browser drops use OLE drag-and-drop directly:
`RegisterClipboardFormat`, `IDataObject::GetData`, `ReleaseStgMedium`, and
`GlobalLock`/`GlobalSize` for `HGLOBAL` blocks.

## Notes

- Files already in the shelf's folder aren't copied onto themselves.
- Shows up to 100 items, newest first. Hidden and system files are left out.
- The bytes of a browser drop have to be taken while the drop handler is
  still running — the source frees its data object as soon as it returns —
  so a big image from a slow site can stall the UI for a moment. Writing
  them, and any download, happens afterwards in the background.
- Folders inside a virtual-file drop are skipped; there's nothing to write.
- Default size 3x2, smallest 2x2.
