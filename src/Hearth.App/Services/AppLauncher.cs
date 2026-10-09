using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Hearth.App.Tablet;
using Hearth.Core.Diagnostics;
using Hearth.Core.Shell;

namespace Hearth.App.Services;

/// <summary>
/// Launches apps and makes sure something visibly happens.
///
/// Single-instance apps (Electron apps such as Antigravity IDE and VS Code,
/// and many others) start a second process that hands over to the copy
/// already running and exits. The running copy then tries to bring its
/// window forward, and Windows refuses, because the launch came from Hearth,
/// a background window: nothing appears, and the app seems not to launch.
/// Measured: the new "Antigravity IDE" process appeared 140 ms after the
/// launch, and the existing window stayed behind, even when any process was
/// allowed to take the foreground.
///
/// So Hearth works out which program the item starts (the .exe, or the
/// target of its shortcut or Start entry) and, if no window of that program
/// comes to the front soon after the launch, brings the program's existing
/// window forward itself (Hearth may do that; it has just handled
/// the click). Apps that open a new window each time (browsers, File
/// Explorer) show that window, and nothing extra happens.
/// </summary>
internal static class AppLauncher
{
    /// <summary>How long a window of the program has to appear on its own before Hearth helps.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// How long Hearth keeps watching for a window at all.
    ///
    /// The first version gave up after 1.8 s, which is fine for an app that
    /// hands over to a running copy (measured at 140 ms) but not for one
    /// starting cold - the user reported having to fall back to Windows' own
    /// Start menu to get Antigravity IDE up, which is what an app that takes
    /// longer than the watch window looks like. Watching for longer costs
    /// nothing: the loop stops the moment the window appears, and it stops
    /// immediately if the user has moved on to something else.
    /// </summary>
    private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(12);

    public static bool Launch(LauncherItem item)
    {
        Hosting.StartTrigger.ClaimForegroundRight();
        var program = ShellLauncher.ProgramPathOf(item);
        var foregroundBefore = WindowTools.GetForegroundWindow();

        if (!ShellLauncher.Launch(item))
        {
            Log.Write($"launch: shell refused '{item.DisplayName}'");
            return false;
        }

        // Store apps and documents: nothing to follow.
        if (program is not null) _ = FollowUpAsync(item.DisplayName, program, foregroundBefore, Dispatcher.CurrentDispatcher);
        return true;
    }

    /// <summary>
    /// Watches for the program's window to come to the front, and brings it
    /// forward itself if it does not. Only the program the item points at
    /// counts: an earlier version followed any process that started meanwhile,
    /// and once picked a script's sleep.exe.
    ///
    /// Nothing is ever taken from the user. The moment the foreground is
    /// something that is neither the window we launched from nor one of
    /// Hearth's own, they have chosen to be elsewhere and the watch stops.
    /// </summary>
    private static async Task FollowUpAsync(string name, string program, IntPtr foregroundBefore, Dispatcher dispatcher)
    {
        try
        {
            var programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { program };
            var clock = Stopwatch.StartNew();
            var helped = false;

            while (clock.Elapsed < GiveUp)
            {
                await Task.Delay(120).ConfigureAwait(false);

                var outcome = await dispatcher.InvokeAsync(() =>
                {
                    if (ForegroundIsOneOf(programs, foregroundBefore)) return Outcome.Arrived;

                    // The user went somewhere else of their own accord.
                    var foreground = WindowTools.GetForegroundWindow();
                    if (foreground != IntPtr.Zero && foreground != foregroundBefore &&
                        !WindowTools.IsOwnProcess(foreground) && !programs.Contains(WindowTools.ProcessPath(foreground)))
                        return Outcome.UserMovedOn;

                    // Give the program a moment to put its own window up first;
                    // an app that can raise itself should be left to do it.
                    if (clock.Elapsed < Settle) return Outcome.Waiting;

                    var existing = WindowTools.SwitchableWindows()
                        .FirstOrDefault(w => programs.Contains(WindowTools.ProcessPath(w)));
                    if (existing == IntPtr.Zero) return Outcome.Waiting;
                    if (existing == WindowTools.GetForegroundWindow()) return Outcome.Arrived;

                    Log.Write($"launch: '{name}' did not come forward after {clock.ElapsedMilliseconds} ms; " +
                              $"bringing '{WindowTools.Title(existing)}' forward");
                    WindowTools.Activate(existing);
                    return Outcome.Helped;
                });

                if (outcome == Outcome.Helped) { helped = true; continue; }
                if (outcome != Outcome.Waiting) return;
            }

            if (!helped) Log.Write($"launch: no window of '{name}' appeared within {GiveUp.TotalSeconds:F0} s");
        }
        catch (Exception ex)
        {
            Log.Error($"launch follow-up '{name}'", ex);
        }
    }

    private enum Outcome
    {
        Waiting,
        Arrived,
        Helped,
        UserMovedOn,
    }

    private static bool ForegroundIsOneOf(HashSet<string> programs, IntPtr foregroundBefore)
    {
        var foreground = WindowTools.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == foregroundBefore) return false;
        return programs.Contains(WindowTools.ProcessPath(foreground));
    }
}
