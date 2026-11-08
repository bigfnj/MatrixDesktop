namespace MatrixDesktop;

[Flags]
internal enum PauseReason
{
    None = 0, Manual = 1, Session = 2, Suspend = 4, DisplayOff = 8,
    Fullscreen = 16, Battery = 32, ShellRecovery = 64,
}

// Independent causes must survive unrelated resume events.
internal sealed class PlaybackPolicy
{
    internal PauseReason Reasons { get; private set; }
    internal bool Paused => Reasons != PauseReason.None;
    internal bool Set(PauseReason reason, bool enabled)
    {
        var before = Reasons;
        Reasons = enabled ? Reasons | reason : Reasons & ~reason;
        return before != Reasons;
    }

    // Only a lock, sleep or display-off hides and suspends the renderer. Every other pause
    // (tray, fullscreen elsewhere, battery, shell recovery) freezes the page and leaves it
    // visible, so the desktop keeps the frozen frame instead of black or the static wallpaper.
    internal static bool SuspendsRenderer(PauseReason reasons) =>
        (reasons & (PauseReason.Session | PauseReason.Suspend | PauseReason.DisplayOff)) != 0;

    internal static string ResolvePriority(string requested, bool wallpaper) =>
        requested == "auto" ? wallpaper ? "idle" : "normal" : requested;

    internal static bool CoversAnyMonitor(System.Drawing.Rectangle foreground, IEnumerable<System.Drawing.Rectangle> monitors) =>
        monitors.Any(monitor => foreground.Left <= monitor.Left && foreground.Top <= monitor.Top
            && foreground.Right >= monitor.Right && foreground.Bottom >= monitor.Bottom);
}

// Process priority is a request. Without SeIncreaseBasePriorityPrivilege Windows applies High
// for RealTime and throws nothing, and Process caches the class it was given, so only a read
// after Refresh shows what Windows applied.
internal interface IPriorityTarget
{
    System.Diagnostics.ProcessPriorityClass PriorityClass { get; set; }
    void Refresh();
}

internal static class PriorityPolicy
{
    internal static System.Diagnostics.ProcessPriorityClass ClassOf(string value) => value switch
    {
        "idle" => System.Diagnostics.ProcessPriorityClass.Idle, "below-normal" => System.Diagnostics.ProcessPriorityClass.BelowNormal,
        "above-normal" => System.Diagnostics.ProcessPriorityClass.AboveNormal, "high" => System.Diagnostics.ProcessPriorityClass.High,
        "realtime" => System.Diagnostics.ProcessPriorityClass.RealTime, _ => System.Diagnostics.ProcessPriorityClass.Normal,
    };

    internal static void Apply(System.Diagnostics.ProcessPriorityClass requested, IPriorityTarget target, Action<string> report)
    {
        target.PriorityClass = requested;
        target.Refresh();
        var granted = target.PriorityClass;
        if (granted != requested) report($"Process priority {requested} was not granted; Windows applied {granted}.");
    }
}

internal sealed class ProcessPriorityTarget(System.Diagnostics.Process process) : IPriorityTarget
{
    public System.Diagnostics.ProcessPriorityClass PriorityClass { get => process.PriorityClass; set => process.PriorityClass = value; }
    public void Refresh() => process.Refresh();
}

// What the host does when its renderer window closes on its own. A renderer that failed to
// start has already shown its error dialog; rebuilding it every 500 ms put that dialog back
// forever in wallpaper mode, so it exits as every other mode does. Any other wallpaper close
// is rebuilt with a capped backoff that resets once a renderer is ready.
internal readonly record struct SurfaceCloseDecision(bool Exit, int RetryAfterMs);

internal static class SurfaceClosePolicy
{
    internal static int RetryDelayMs(int consecutiveFailures) =>
        Math.Min(10000, 500 * (1 << Math.Min(4, Math.Max(0, consecutiveFailures))));

    internal static SurfaceCloseDecision Decide(bool wallpaper, bool startupFailed, int consecutiveFailures) =>
        wallpaper && !startupFailed ? new(false, RetryDelayMs(consecutiveFailures)) : new(true, 0);
}
