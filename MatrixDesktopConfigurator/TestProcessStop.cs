using System;
using System.Diagnostics;

namespace MatrixDesktopConfigurator;

// AUD-74: every MatrixDesktop instance joins the WebView2 browser process of whichever host
// started first, and that browser is a child of that host. Killing the Test host's whole
// process tree could take the shared browser, and every other instance's rendering, with it.
// Stop asks the window to close, waits, then ends the Test host alone; WebView2 shuts its
// browser down once the last host has gone.
internal static class TestProcessStop
{
    public const int WaitMilliseconds = 1500;

    public static void Run(Process process) => Run(
        () => process.HasExited,
        () => process.CloseMainWindow(),
        process.WaitForExit,
        entireProcessTree => process.Kill(entireProcessTree));

    public static void Run(Func<bool> hasExited, Action closeMainWindow, Func<int, bool> waitForExit, Action<bool> kill)
    {
        if (hasExited())
        {
            return;
        }

        try
        {
            closeMainWindow();
        }
        catch
        {
            // Ignore.
        }

        if (!waitForExit(WaitMilliseconds))
        {
            kill(false);
            waitForExit(WaitMilliseconds);
        }
    }

    // AUD-43: a Test window closed by the user ends the host without a word to the page, so
    // Stop Test stayed enabled. Subscribe before enabling, so an early exit is not missed.
    public static void WatchExit(Process process, Action<Process, int> onExited)
    {
        var pid = process.Id;
        process.Exited += (_, _) => onExited(process, pid);
        process.EnableRaisingEvents = true;
    }

    // An exit notice counts only for the Test host still on record. One from a host a newer
    // Test already replaced must not clear the newer one, or Stop could no longer end it.
    public static bool ClaimExited(ref Process? current, Process exited)
    {
        if (!ReferenceEquals(current, exited))
        {
            return false;
        }

        current = null;
        return true;
    }
}
