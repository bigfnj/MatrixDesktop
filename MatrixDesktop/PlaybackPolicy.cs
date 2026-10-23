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

    internal static string ResolvePriority(string requested, bool wallpaper) =>
        requested == "auto" ? wallpaper ? "idle" : "normal" : requested;

    internal static bool CoversAnyMonitor(System.Drawing.Rectangle foreground, IEnumerable<System.Drawing.Rectangle> monitors) =>
        monitors.Any(monitor => foreground.Left <= monitor.Left && foreground.Top <= monitor.Top
            && foreground.Right >= monitor.Right && foreground.Bottom >= monitor.Bottom);
}
