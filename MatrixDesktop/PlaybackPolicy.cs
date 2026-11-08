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

    // 2026-11-08 (AUD-09): the hidden coordinator receives every broadcast. Only the ones
    // that change what the native resources read may rebuild them: the wallpaper, the
    // desktop pattern and high contrast (COLOR_BACKGROUND), font smoothing (atlases follow
    // it at DEFAULT_QUALITY), system colours and the theme. Environment, Policy and
    // ImmersiveColorSet broadcasts carry wParam 0 and are ignored.
    internal static bool ShouldRefreshNativeResources(int message, nint wParam, bool refreshable) => refreshable && message switch
    {
        0x001A => wParam is 0x0014 or 0x0015 or 0x0043 or 0x004B or 0x200B or 0x200D or 0x2013,
        0x0015 or 0x031A => true,
        _ => false,
    };
}
