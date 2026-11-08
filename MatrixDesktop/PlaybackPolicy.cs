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

// What the host does when a WebView2 process fails. Nothing handled ProcessFailed, so a lost
// renderer left a long-running wallpaper blank until an unrelated shell event. Per WebView2's
// guidance: a lost browser process needs a new WebView, a lost main-frame renderer can be
// reloaded, an unresponsive renderer is reported again about every 15 s until it answers, and
// WebView2 restarts every other process itself. WebView2 reports a renderer unresponsive only
// when input or a navigation goes unanswered, so only fullscreen and windowed hosts get that
// report: the wallpaper and preview windows are disabled and never navigate while hung, so a
// hung page there stays frozen until a display change, an Explorer restart or a relaunch.
// These kinds are this file's own, so the core harness can test the table without WebView2.
internal enum RendererFailureKind { Other, BrowserProcessExited, RenderProcessExited, RenderProcessUnresponsive }

internal enum RendererFailureAction { LogOnly, Reload, Recreate, Exit }

internal sealed class RendererFailurePolicy
{
    internal const int ReloadBudget = 3;
    internal const long ReloadWindowMs = 10 * 60 * 1000;
    internal const int UnresponsiveLimit = 3;
    // A longer gap than two report intervals means the renderer answered in between.
    internal const long UnresponsiveGapMs = 30_000;
    // The host's rebuild backoff starts over only after a renderer stayed ready this long, so one
    // that fails right after every load backs off instead of being rebuilt every 500 ms.
    internal const long HealthyAfterMs = 60_000;

    private readonly Queue<long> _reloads = new();
    private int _unresponsive;
    private long _lastUnresponsiveMs;

    internal RendererFailureAction Decide(RendererFailureKind kind, bool wallpaper, long nowMs)
    {
        switch (kind)
        {
            case RendererFailureKind.BrowserProcessExited:
                return Lost(wallpaper);
            case RendererFailureKind.RenderProcessExited:
                return Reload(wallpaper, nowMs);
            case RendererFailureKind.RenderProcessUnresponsive:
                _unresponsive = _unresponsive > 0 && nowMs - _lastUnresponsiveMs <= UnresponsiveGapMs ? _unresponsive + 1 : 1;
                _lastUnresponsiveMs = nowMs;
                return _unresponsive < UnresponsiveLimit ? RendererFailureAction.LogOnly : Reload(wallpaper, nowMs);
            default:
                return RendererFailureAction.LogOnly;
        }
    }

    internal static bool StayedHealthy(bool ready, long readySinceMs, long nowMs) => ready && nowMs - readySinceMs >= HealthyAfterMs;

    // Reloads are budgeted, so a renderer that dies on every load is rebuilt (wallpaper, with the
    // host's capped backoff) or ends the host instead of reloading in a tight loop.
    private RendererFailureAction Reload(bool wallpaper, long nowMs)
    {
        while (_reloads.Count > 0 && nowMs - _reloads.Peek() >= ReloadWindowMs) _reloads.Dequeue();
        if (_reloads.Count >= ReloadBudget) return Lost(wallpaper);
        _unresponsive = 0;
        _reloads.Enqueue(nowMs);
        return RendererFailureAction.Reload;
    }

    private RendererFailureAction Lost(bool wallpaper)
    {
        _unresponsive = 0;
        return wallpaper ? RendererFailureAction.Recreate : RendererFailureAction.Exit;
    }
}

// R2-01. WinForms raises FormClosing for WM_QUERYENDSESSION as well as WM_CLOSE, and the
// shutdown that query asks about can still be cancelled. A top-level surface that tore down its
// WebView there stayed black after WM_ENDSESSION(FALSE), because nothing rebuilds it. MainForm
// marks the query's dispatch itself: e.CloseReason stays WindowsShutDown after a cancel.
// WM_ENDSESSION(TRUE) raises FormClosed and reaches Dispose, which tear down.
internal static class HostClosePolicy
{
    internal static bool TearsDownOnClosing(bool sessionEndQuery) => !sessionEndQuery;
}
