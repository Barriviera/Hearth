# Hearth's bar

Optional. Nothing in here starts until `bar.json` says `Enabled: true`, and the
Windows taskbar is untouched until then. Hearth has always run alongside it and
still can.

## Files

| file | what it holds |
|---|---|
| `BarService.cs` | owns everything: settings, the shared window list, a window (or a Halo) per display, the app and bar menus, and app colours |
| `WindowList.cs` | the live list of windows, shared by every style |
| `BarWindow.cs` | the docked strip or floating capsule, and its app-bar registration |
| `BarParts.cs` | displays, colours, the app button, the notification area, the clock |
| `Presentations.cs` | Taskbar, Panel, Menu bar, Dock and Docklike |
| `Halo.cs` | the ring around the screen's border, its capsule and its corner |
| `ShellTaskbar.cs` | taking the Windows taskbar off the screen, and putting it back |

Settings live in `Hearth.Core/Settings/BarSettings.cs`
(`%AppData%\Hearth\bar.json`); the settings page is
`Views/SettingsScreen/SettingsWindow.Bar.cs`.

## The five styles

They are meant as five different ideas about what a bar is for, not one bar
with five skins.

- **Taskbar** — Start, one labelled button per window (or per application), the
  tray and the clock. What a Windows user already knows how to drive.
- **Panel** — a Linux panel: an Applications button, a window list, and applets,
  which here means Hearth's own widgets shrunk into the strip.
- **Menu bar and dock** — two windows. A thin menu bar along the top that
  reserves its strip and carries the status items and the name of whatever is
  in front, and a dock floating at the other edge that sizes itself to its
  contents and magnifies under the pointer.
- **Docklike** — one square per application rather than per window, with a dot
  for each window. A stable row you learn the positions of.
- **Halo** — no bar. The screen's border is the bar: the perimeter is one line
  of length 2(w+h) running clockwise from the top-left, and each running
  application owns an arc of it, coloured with its own icon's dominant colour.
  It reserves no work area and moves no window. Rest the pointer on an edge and
  the arc under it blooms open. A small corner capsule carries Start, the tray
  and the clock, because those cannot be coloured lengths.

## Rules anything added here has to keep

- **Never take the foreground.** WS_EX_NOACTIVATE, and MA_NOACTIVATE for every
  click. A bar that activated would be the window in front, so its own buttons
  would be lying about which window that is.
- **Call `ForeignPopups.DismissForClick()` from WM_MOUSEACTIVATE.** A
  non-activating surface has to dismiss other programs' menus by hand.
- **Never block the UI thread on `SHAppBarMessage`.** ABM_NEW, ABM_SETPOS and
  ABM_REMOVE wait one to two seconds while Explorer reflows every window. Place
  the window where it will end up first, claim the strip from `Task.Run`.
- **Refresh runs on every window change.** Anything expensive — reading the
  tray, extracting an icon, working out a colour — is cached across rebuilds,
  not done again. `BarPresentation.Tray`/`Clock`/`Detach` is the pattern.
- **Everything done to the shell is written to `shell-state.json` first**, so
  the watchdog, `Hearth.exe --restore-shell` and the next start can all undo
  it. The bar must leave the Windows taskbar exactly as it found it.

## Testing

`Hearth.exe --bar on|off|toggle|settings|taskbar|panel|menubar|docklike|halo`
switches the bar and its style in the running copy, which is how it is
exercised without clicking through settings. The log lines to look for are
`bar: started`, `bar: <edge> strip at ...` and `bar: Windows taskbar
hidden by region`.
