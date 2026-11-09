using System.Text.RegularExpressions;

namespace MatrixDesktop.Tests;

// Host startup, storage and lifecycle decisions that the WinForms host delegates to pure code.
internal static class HostLifecyclePolicyTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Host WebView2 profile record keeps the format the gate parses", ProfileRecordFormat),
        ("Host write probe stays writable while another instance holds its probe open", OverlappingProbes),
        ("Host write probe still reports a write-denied folder as unwritable", DeniedProbe),
        ("Host storage warns when it falls back past the preferred base folder", FallbackWarning),
        ("Host priority reports a class Windows did not grant", PriorityReadBack),
        ("Host pause keeps the frozen frame visible unless lock, sleep or display-off took it away", PauseSuspension),
        ("Host wallpaper does not rebuild a renderer that failed to start", SurfaceClose),
        ("Host wallpaper guard lets the newest launch take over and leaves other scopes alone", WallpaperTakeover),
        ("Host wallpaper guard scope keeps isolated test launches away from the owner's wallpaper", WallpaperScope),
        ("Host wallpaper guard that cannot open its objects lets the launch run and says why", WallpaperGuardUnavailable),
        ("Host renderer process failure reloads, rebuilds or exits as WebView2 requires", RendererFailure),
        ("Host answers a session-end query without tearing down its renderer", SessionEndQuery),
        ("Host wallpaper guard withdraws a request the holder had not claimed by the newcomer's deadline", WallpaperTakeoverWithdrawn),
        ("Host wallpaper guard lets the newer of two overlapping launches win", WallpaperNewerOfTwoWins),
    ];

    // R2-04. A newcomer that timed out left its request standing, so a holder slower than the
    // bound exited afterwards anyway and left no wallpaper at all.
    private static void WallpaperTakeoverWithdrawn()
    {
        var scope = "test-" + Guid.NewGuid().ToString("N");
        using var holding = new ManualResetEventSlim();
        using var asked = new ManualResetEventSlim();
        using var newcomerDone = new ManualResetEventSlim();
        using var decided = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var claimed = true;
        var holder = new Thread(() =>
        {
            using var held = WallpaperInstanceGuard.Acquire(scope, TimeSpan.FromSeconds(1));
            held.ListenForTakeover(asked.Set);
            holding.Set();
            // Slower than the newcomer's bound: the holder reaches the request only afterwards.
            newcomerDone.Wait(TimeSpan.FromSeconds(20));
            claimed = held.ClaimTakeover();
            decided.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        }) { IsBackground = true };
        holder.Start();
        Check.True(holding.Wait(TimeSpan.FromSeconds(10)), "the holder must acquire");
        WallpaperInstanceGuard.Outcome late;
        using (var newcomer = WallpaperInstanceGuard.Acquire(scope, TimeSpan.FromMilliseconds(700))) late = newcomer.Result;
        newcomerDone.Set();
        Check.True(decided.Wait(TimeSpan.FromSeconds(10)), "the holder must reach the request");
        Check.True(asked.IsSet, "the newcomer must have asked the holder to exit");
        Check.Equal(WallpaperInstanceGuard.Outcome.TimedOut, late, "a holder that does not hand over in time keeps the desktop");
        Check.False(claimed, "a request the newcomer withdrew at its deadline must not make the slow holder exit");
        using (var scopeMutex = Mutex.OpenExisting($"Local\\MatrixDesktop.Wallpaper.{scope}"))
        {
            var taken = false;
            try { taken = scopeMutex.WaitOne(0); } catch (AbandonedMutexException) { taken = true; }
            if (taken) scopeMutex.ReleaseMutex();
            Check.False(taken, "the holder must still own the scope after the newcomer gave up");
        }
        release.Set();
        Check.True(holder.Join(TimeSpan.FromSeconds(10)), "the holder must finish");
    }

    // R2-04. Of two overlapping newcomers the first to acquire was ended, so the older launch
    // often won. The holder here claims and takes 300 ms to tear down, as a real one does.
    private static void WallpaperNewerOfTwoWins()
    {
        for (var trial = 0; trial < 12; trial++)
        {
            var scope = "test-" + Guid.NewGuid().ToString("N");
            var offset = trial * 20;
            using var holding = new ManualResetEventSlim();
            WallpaperInstanceGuard.Outcome older = default, newer = default;
            var newerClaimed = false;
            var holder = new Thread(() =>
            {
                using var held = WallpaperInstanceGuard.Acquire(scope, TimeSpan.FromSeconds(1));
                using var asked = new ManualResetEventSlim();
                held.ListenForTakeover(asked.Set);
                holding.Set();
                if (asked.Wait(TimeSpan.FromSeconds(10)) && held.ClaimTakeover()) Thread.Sleep(300);
            }) { IsBackground = true };
            holder.Start();
            Check.True(holding.Wait(TimeSpan.FromSeconds(10)), "the holder must acquire");
            var first = new Thread(() =>
            {
                using var guard = WallpaperInstanceGuard.Acquire(scope, TimeSpan.FromSeconds(10));
                older = guard.Result;
            }) { IsBackground = true };
            var second = new Thread(() =>
            {
                using var guard = WallpaperInstanceGuard.Acquire(scope, TimeSpan.FromSeconds(10));
                newer = guard.Result;
                using var asked = new ManualResetEventSlim();
                guard.ListenForTakeover(asked.Set);
                if (asked.Wait(300)) newerClaimed = guard.ClaimTakeover();
            }) { IsBackground = true };
            first.Start();
            Thread.Sleep(offset);
            second.Start();
            Check.True(first.Join(TimeSpan.FromSeconds(15)) && second.Join(TimeSpan.FromSeconds(15)) && holder.Join(TimeSpan.FromSeconds(15)), $"all three launches must finish (trial {trial})");
            Check.Equal(WallpaperInstanceGuard.Outcome.TookOver, newer, $"the newer launch must take over (trial {trial}, {offset} ms apart)");
            Check.Equal(WallpaperInstanceGuard.Outcome.Superseded, older, $"the older newcomer must stop quietly and never take over (trial {trial}, {offset} ms apart)");
            Check.False(newerClaimed, $"nothing may ask the winner to leave once the older newcomer stopped (trial {trial})");
        }
    }

    // R2-01. A fullscreen, windowed or screensaver surface disposed its WebView on
    // WM_QUERYENDSESSION and stayed black after WM_ENDSESSION(FALSE). MainForm's use of this
    // policy is checked on the published dll by HostWiring.
    private static void SessionEndQuery()
    {
        Check.False(HostClosePolicy.TearsDownOnClosing(sessionEndQuery: true), "a session-end query can still be cancelled, so it must not tear down the renderer");
        Check.True(HostClosePolicy.TearsDownOnClosing(sessionEndQuery: false), "an ordinary close (WM_CLOSE, Close) must still tear down before the handles go");
    }

    // AUD-07. Nothing handled CoreWebView2.ProcessFailed, so a lost renderer left a wallpaper
    // blank until an unrelated shell event. The host side (MainForm subscribes and forwards, the
    // context asks this policy) is checked on the published dll by HostWiring.
    private static void RendererFailure()
    {
        const long start = 1_000_000;
        foreach (var wallpaper in new[] { true, false })
            Check.Equal(RendererFailureAction.Reload, new RendererFailurePolicy().Decide(RendererFailureKind.RenderProcessExited, wallpaper, start),
                $"a lost main-frame renderer must be reloaded (wallpaper={wallpaper})");
        Check.Equal(RendererFailureAction.Recreate, new RendererFailurePolicy().Decide(RendererFailureKind.BrowserProcessExited, true, start),
            "a lost browser process closes the WebView, so a wallpaper must rebuild its surface");
        Check.Equal(RendererFailureAction.Exit, new RendererFailurePolicy().Decide(RendererFailureKind.BrowserProcessExited, false, start),
            "a lost browser process ends a fullscreen, screensaver or preview host");
        foreach (var wallpaper in new[] { true, false })
            Check.Equal(RendererFailureAction.LogOnly, new RendererFailurePolicy().Decide(RendererFailureKind.Other, wallpaper, start),
                $"WebView2 restarts GPU, utility and other processes itself (wallpaper={wallpaper})");

        // An unresponsive renderer is reported again about every 15 s while it stays hung. R2-05:
        // WebView2 reports it only for unanswered input or navigation, so only a fullscreen or
        // windowed host (wallpaper: false) gets it; the disabled wallpaper window never does.
        var hung = new RendererFailurePolicy();
        Check.Equal(RendererFailureAction.LogOnly, hung.Decide(RendererFailureKind.RenderProcessUnresponsive, false, start), "one unresponsive report only logs");
        Check.Equal(RendererFailureAction.LogOnly, hung.Decide(RendererFailureKind.RenderProcessUnresponsive, false, start + 15_000), "two unresponsive reports only log");
        Check.Equal(RendererFailureAction.Reload, hung.Decide(RendererFailureKind.RenderProcessUnresponsive, false, start + 30_000), "three consecutive unresponsive reports reload a fullscreen or windowed page");
        Check.Equal(RendererFailureAction.LogOnly, hung.Decide(RendererFailureKind.RenderProcessUnresponsive, false, start + 45_000), "a reload starts the unresponsive count over");
        var answered = new RendererFailurePolicy();
        answered.Decide(RendererFailureKind.RenderProcessUnresponsive, false, start);
        answered.Decide(RendererFailureKind.RenderProcessUnresponsive, false, start + 15_000);
        Check.Equal(RendererFailureAction.LogOnly, answered.Decide(RendererFailureKind.RenderProcessUnresponsive, false, start + 15_000 + RendererFailurePolicy.UnresponsiveGapMs + 1),
            "a renderer that answered between reports (a gap over two intervals) starts a new count");

        // Reloads are budgeted: a renderer that dies on every load is rebuilt or ends the host.
        foreach (var (wallpaper, exhausted) in new[] { (true, RendererFailureAction.Recreate), (false, RendererFailureAction.Exit) })
        {
            var crashing = new RendererFailurePolicy();
            for (var i = 0; i < RendererFailurePolicy.ReloadBudget; i++)
                Check.Equal(RendererFailureAction.Reload, crashing.Decide(RendererFailureKind.RenderProcessExited, wallpaper, start + i * 1000), $"reload {i + 1} is within the budget (wallpaper={wallpaper})");
            Check.Equal(exhausted, crashing.Decide(RendererFailureKind.RenderProcessExited, wallpaper, start + 5000), $"a failure past the reload budget must not reload again (wallpaper={wallpaper})");
            Check.Equal(RendererFailureAction.Reload, crashing.Decide(RendererFailureKind.RenderProcessExited, wallpaper, start + RendererFailurePolicy.ReloadWindowMs),
                $"the budget refills once the first reload is ten minutes old (wallpaper={wallpaper})");
        }
        Check.Equal(3, RendererFailurePolicy.ReloadBudget, "three reloads per window");
        Check.Equal(600_000L, RendererFailurePolicy.ReloadWindowMs, "the reload window is ten minutes");

        Check.False(RendererFailurePolicy.StayedHealthy(true, start, start + 59_999), "a renderer ready for under a minute keeps the rebuild backoff");
        Check.True(RendererFailurePolicy.StayedHealthy(true, start, start + 60_000), "a renderer ready for a minute resets the rebuild backoff");
        Check.False(RendererFailurePolicy.StayedHealthy(false, start, start + 600_000), "a renderer that is not ready never resets the backoff");
    }

    // An elevated wallpaper creates the guard's objects with a High label, so a normal launch is
    // refused when it opens them; that must not throw out of the launch. A different-type object
    // under the event name is the deterministic stand-in for any object this launch cannot open.
    private static void WallpaperGuardUnavailable()
    {
        var scope = "test-" + Guid.NewGuid().ToString("N");
        using var squatter = new Mutex(false, $"Local\\MatrixDesktop.Wallpaper.{scope}.Exit");
        using var guard = WallpaperInstanceGuard.Acquire(scope, TimeSpan.FromSeconds(1));
        Check.Equal(WallpaperInstanceGuard.Outcome.Unavailable, guard.Result, "a guard that cannot open its objects must report itself unavailable instead of failing the launch");
        Check.True(!string.IsNullOrWhiteSpace(guard.Problem), "the unavailable guard must say why, for the launch's warning");
        guard.ListenForTakeover(() => throw new InvalidOperationException("an unavailable guard has nothing to listen to"));
    }

    // AUD-72. A second --wallpaper stacked another renderer and tray icon under the first. Every
    // scope here is a fresh GUID, so this never signals a real wallpaper ("default" scope).
    private static void WallpaperTakeover()
    {
        var scope = "test-" + Guid.NewGuid().ToString("N");
        using var takeoverRequested = new ManualResetEventSlim();
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var staleRequest = new ManualResetEventSlim();
        WallpaperInstanceGuard.Outcome firstResult = default, secondResult = default;
        bool releasing = false, holderReleasedFirst = false;
        // Each launch on its own thread: a mutex belongs to the thread that acquired it.
        var holder = new Thread(() =>
        {
            using var first = WallpaperInstanceGuard.Acquire(scope, TimeSpan.FromSeconds(1));
            firstResult = first.Result;
            first.ListenForTakeover(takeoverRequested.Set);
            holding.Set();
            release.Wait(TimeSpan.FromSeconds(20));
            Volatile.Write(ref releasing, true);
        }) { IsBackground = true };
        holder.Start();
        Check.True(holding.Wait(TimeSpan.FromSeconds(10)), "the first wallpaper must acquire");
        Check.Equal(WallpaperInstanceGuard.Outcome.Acquired, firstResult, "the first launch in a scope acquires without waiting");

        using (var other = WallpaperInstanceGuard.Acquire("test-" + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(1)))
            Check.Equal(WallpaperInstanceGuard.Outcome.Acquired, other.Result, "a launch in another scope (an isolated gate or test run) must acquire at once");
        Check.False(takeoverRequested.Wait(300), "another scope must never ask this scope's wallpaper to exit");

        var newcomer = new Thread(() =>
        {
            using var second = WallpaperInstanceGuard.Acquire(scope, TimeSpan.FromSeconds(10));
            secondResult = second.Result;
            holderReleasedFirst = Volatile.Read(ref releasing);
            second.ListenForTakeover(staleRequest.Set);
            Thread.Sleep(400);
        }) { IsBackground = true };
        newcomer.Start();
        Check.True(takeoverRequested.Wait(TimeSpan.FromSeconds(5)), "a second wallpaper launch in the same scope must ask the running one to exit");
        // A holder takes a moment to tear down; the newcomer keeps waiting and asking meanwhile.
        Thread.Sleep(600);
        Check.True(newcomer.IsAlive, "the newcomer must wait for the holder instead of stacking a second wallpaper");
        release.Set();
        Check.True(newcomer.Join(TimeSpan.FromSeconds(10)) && holder.Join(TimeSpan.FromSeconds(10)), "both launches must finish");
        Check.Equal(WallpaperInstanceGuard.Outcome.TookOver, secondResult, "the newest launch must take over once the holder released");
        Check.True(holderReleasedFirst, "the newcomer must acquire only after the holder released");
        Check.False(staleRequest.IsSet, "a request aimed at the previous holder must not end the new one");

        // A holder that never hands over keeps the desktop; the newcomer gives up at its bound.
        var stuckScope = "test-" + Guid.NewGuid().ToString("N");
        using var stuckHolding = new ManualResetEventSlim();
        using var stuckRelease = new ManualResetEventSlim();
        var stuck = new Thread(() =>
        {
            using var held = WallpaperInstanceGuard.Acquire(stuckScope, TimeSpan.FromSeconds(1));
            stuckHolding.Set();
            stuckRelease.Wait(TimeSpan.FromSeconds(20));
        }) { IsBackground = true };
        stuck.Start();
        Check.True(stuckHolding.Wait(TimeSpan.FromSeconds(10)), "the unresponsive holder must acquire");
        var waited = System.Diagnostics.Stopwatch.StartNew();
        using (var late = WallpaperInstanceGuard.Acquire(stuckScope, TimeSpan.FromMilliseconds(700)))
        {
            Check.Equal(WallpaperInstanceGuard.Outcome.TimedOut, late.Result, "a holder that does not hand over in time keeps the desktop");
            Check.True(waited.ElapsedMilliseconds >= 600, $"the newcomer must wait out its bound before stopping (waited {waited.ElapsedMilliseconds} ms)");
        }
        stuckRelease.Set();
        Check.True(stuck.Join(TimeSpan.FromSeconds(10)), "the unresponsive holder must finish");
    }

    private static void WallpaperScope()
    {
        Check.Equal("default", WallpaperInstanceGuard.Scope(_ => null), "an ordinary launch uses the owner's scope");
        Check.Equal("default", WallpaperInstanceGuard.Scope(_ => ""), "empty isolation variables do not isolate");
        Check.True(WallpaperInstanceGuard.IsolationVariables.Contains("WEBVIEW2_USER_DATA_FOLDER"), "the gate's and the smokes' profile variable must isolate");
        var seen = new HashSet<string>();
        foreach (var name in WallpaperInstanceGuard.IsolationVariables)
        {
            var scope = WallpaperInstanceGuard.Scope(variable => variable == name ? @"D:\gate run\artifacts\gate\webview-profile" : null);
            Check.False(scope == "default", $"a launch that sets {name} must never share the owner's wallpaper scope");
            Check.True(seen.Add(scope), $"{name} must give a scope of its own");
        }
        string Profile(string folder) => WallpaperInstanceGuard.Scope(variable => variable == "WEBVIEW2_USER_DATA_FOLDER" ? folder : null);
        Check.Equal(Profile(@"D:\a"), Profile(@"D:\a"), "the same isolation must give the same scope, so test launches still replace each other");
        Check.False(Profile(@"D:\a") == Profile(@"D:\b"), "different isolated profiles are different scopes");
    }

    // AUD-05. The startup error dialog came back every 500 ms in wallpaper mode. The host side
    // (FailStartup sets the flag, OnSurfaceClosed asks this policy) is checked on the published
    // dll by HostWiring.
    private static void SurfaceClose()
    {
        var failed = SurfaceClosePolicy.Decide(wallpaper: true, startupFailed: true, consecutiveFailures: 0);
        Check.True(failed.Exit, "a wallpaper renderer that failed to start must exit after its dialog, never be rebuilt to show it again");
        var lost = SurfaceClosePolicy.Decide(wallpaper: true, startupFailed: false, consecutiveFailures: 0);
        Check.False(lost.Exit, "any other wallpaper close must still be rebuilt");
        Check.Equal(500, lost.RetryAfterMs, "the first rebuild keeps the 500 ms delay");
        Check.Equal(1000, SurfaceClosePolicy.Decide(true, false, 1).RetryAfterMs, "repeated closes back off");
        Check.Equal(8000, SurfaceClosePolicy.Decide(true, false, 40).RetryAfterMs, "the backoff is capped");
        Check.True(SurfaceClosePolicy.Decide(false, false, 0).Exit, "a closed standard, screensaver or preview window exits");
        Check.True(SurfaceClosePolicy.Decide(false, true, 0).Exit, "a failed standard start exits");
    }

    // AUD-06. Hiding and suspending the renderer for every pause put black or the static
    // wallpaper on the desktop for a tray pause, a fullscreen app on another monitor or battery.
    private static void PauseSuspension()
    {
        const PauseReason hiding = PauseReason.Session | PauseReason.Suspend | PauseReason.DisplayOff;
        var flags = Enum.GetValues<PauseReason>().Where(reason => reason != PauseReason.None).ToArray();
        Check.Equal(7, flags.Length, "the subset sweep must cover every pause reason");
        for (var mask = 0; mask < 1 << flags.Length; mask++)
        {
            var reasons = PauseReason.None;
            for (var bit = 0; bit < flags.Length; bit++) if ((mask & (1 << bit)) != 0) reasons |= flags[bit];
            Check.Equal((reasons & hiding) != 0, PlaybackPolicy.SuspendsRenderer(reasons), $"renderer suspension for '{reasons}'");
        }
        foreach (var visible in new[] { PauseReason.Manual, PauseReason.Fullscreen, PauseReason.Battery, PauseReason.ShellRecovery,
                     PauseReason.Manual | PauseReason.Fullscreen | PauseReason.Battery | PauseReason.ShellRecovery })
            Check.False(PlaybackPolicy.SuspendsRenderer(visible), $"a '{visible}' pause must keep the frozen frame on the desktop");
        foreach (var hidden in new[] { PauseReason.Session, PauseReason.Manual | PauseReason.Session, PauseReason.Suspend, PauseReason.DisplayOff })
            Check.True(PlaybackPolicy.SuspendsRenderer(hidden), $"a '{hidden}' pause must still suspend the renderer");
    }

    // Models System.Diagnostics.Process: the setter caches the requested class, Windows
    // grants High for RealTime without the privilege, and only Refresh exposes the grant.
    private sealed class FakePriorityTarget(Func<System.Diagnostics.ProcessPriorityClass, System.Diagnostics.ProcessPriorityClass> grant) : IPriorityTarget
    {
        private System.Diagnostics.ProcessPriorityClass _cached, _os;
        private bool _haveCached;
        internal List<string> Operations { get; } = [];
        public System.Diagnostics.ProcessPriorityClass PriorityClass
        {
            get { Operations.Add("get"); return _haveCached ? _cached : _os; }
            set { Operations.Add("set:" + value); _cached = value; _haveCached = true; _os = grant(value); }
        }
        public void Refresh() { Operations.Add("refresh"); _haveCached = false; }
    }

    private static void PriorityReadBack()
    {
        var unprivileged = (System.Diagnostics.ProcessPriorityClass requested) =>
            requested == System.Diagnostics.ProcessPriorityClass.RealTime ? System.Diagnostics.ProcessPriorityClass.High : requested;
        var reports = new List<string>();
        var target = new FakePriorityTarget(unprivileged);
        PriorityPolicy.Apply(System.Diagnostics.ProcessPriorityClass.RealTime, target, reports.Add);
        Check.Equal("set:RealTime,refresh,get", string.Join(",", target.Operations), "the class must be read back after Refresh, never from the setter's cache");
        Check.Equal(1, reports.Count, "a downgraded RealTime must be reported exactly once");
        Check.Contains(reports[0], "RealTime", "the report must name the requested class");
        Check.Contains(reports[0], "High", "the report must name the class Windows applied");
        foreach (var value in new[] { "idle", "below-normal", "normal", "above-normal", "high" })
        {
            reports.Clear();
            PriorityPolicy.Apply(PriorityPolicy.ClassOf(value), new FakePriorityTarget(unprivileged), reports.Add);
            Check.Equal(0, reports.Count, $"a granted '{value}' class must not be reported");
        }
        Check.Equal(System.Diagnostics.ProcessPriorityClass.RealTime, PriorityPolicy.ClassOf("realtime"), "the CLI value maps to RealTime");

        // Real Process leg on a child this test owns. Unprivileged tokens (the normal case)
        // must see the downgrade reported; a token holding the privilege cannot, and says so.
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            reports.Clear();
            PriorityPolicy.Apply(System.Diagnostics.ProcessPriorityClass.RealTime, new ProcessPriorityTarget(child), reports.Add);
            using var fresh = System.Diagnostics.Process.GetProcessById(child.Id);
            if (fresh.PriorityClass == System.Diagnostics.ProcessPriorityClass.RealTime)
                Console.WriteLine("NOTE  real-process priority leg UNCHECKED: this token was granted RealTime");
            else
                Check.Equal(1, reports.Count, $"a real unprivileged RealTime request (Windows applied {fresh.PriorityClass}) must be reported");
        }
        finally { try { child.Kill(); child.WaitForExit(5000); } catch { } }
    }

    private static string NewTempFolder() => Path.Combine(Path.GetTempPath(), "md-host-probe-" + Guid.NewGuid().ToString("N"));

    private static void OverlappingProbes()
    {
        var folder = NewTempFolder();
        try
        {
            using var firstOpen = new ManualResetEventSlim();
            using var secondOpen = new ManualResetEventSlim();
            string? firstProbe = null, secondProbe = null;
            // Witnessed schedule: the first probe stays open until the second has opened its
            // own, so a legal serialised run cannot make this pass.
            var first = Task.Run(() => HostStorage.TryEnsureWritableFolder(folder, probe =>
            {
                firstProbe = probe;
                firstOpen.Set();
                secondOpen.Wait(TimeSpan.FromSeconds(5));
            }));
            Check.True(firstOpen.Wait(TimeSpan.FromSeconds(10)), "the first probe must reach its open file");
            var second = HostStorage.TryEnsureWritableFolder(folder, probe => { secondProbe = probe; secondOpen.Set(); });
            Check.True(second, "a probe overlapping another instance's open probe must report the folder writable");
            Check.True(secondOpen.IsSet, "the second probe must open its file while the first is still held");
            Check.True(first.Wait(TimeSpan.FromSeconds(10)) && first.Result, "the held probe must also report the folder writable");
            Check.True(firstProbe is not null && secondProbe is not null && firstProbe != secondProbe, "each attempt must own a distinct probe path");
            Check.Equal(0, Directory.GetFiles(folder).Length, "no probe file may remain after both attempts close");

            // Supplemental: unsynchronised probing from two threads never reports unwritable.
            var failures = 0;
            Parallel.For(0, 2, _ => { for (var i = 0; i < 200; i++) if (!HostStorage.TryEnsureWritableFolder(folder)) Interlocked.Increment(ref failures); });
            Check.Equal(0, failures, "concurrent probes of one writable folder must never report it unwritable");
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch { } }
    }

    private static void DeniedProbe()
    {
        var folder = NewTempFolder();
        Directory.CreateDirectory(folder);
        try
        {
            DenyWrites(folder, deny: true);
            Check.False(HostStorage.TryEnsureWritableFolder(folder), "a folder that refuses new files must report unwritable");
        }
        finally
        {
            DenyWrites(folder, deny: false);
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    private static void FallbackWarning()
    {
        var root = NewTempFolder();
        var denied = Path.Combine(root, "denied");
        var fallback = Path.Combine(root, "fallback");
        Directory.CreateDirectory(Path.Combine(denied, "MatrixDesktop", "WebView2"));
        try
        {
            DenyWrites(Path.Combine(denied, "MatrixDesktop", "WebView2"), deny: true);
            var warnings = new List<string>();
            var chosen = HostStorage.SelectWritableFolder([denied, fallback], "WebView2", warnings.Add);
            Check.Equal(Path.Combine(fallback, "MatrixDesktop", "WebView2"), chosen ?? "(null)", "the next writable base must be chosen");
            Check.Equal(1, warnings.Count, "falling back past the preferred base must warn exactly once");
            Check.Contains(warnings[0], fallback, "the warning must name the folder actually used");
            warnings.Clear();
            Check.Equal(Path.Combine(fallback, "MatrixDesktop", "Web"), HostStorage.SelectWritableFolder([fallback, denied], "Web", warnings.Add) ?? "(null)", "the preferred writable base is used");
            Check.Equal(0, warnings.Count, "using the preferred base must not warn");
        }
        finally
        {
            DenyWrites(Path.Combine(denied, "MatrixDesktop", "WebView2"), deny: false);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // Deny creating files in the folder for the current user, so the probe itself is refused.
    private static void DenyWrites(string folder, bool deny)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(folder);
            var acl = info.GetAccessControl();
            var rule = new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.WriteData,
                System.Security.AccessControl.AccessControlType.Deny);
            if (deny) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(folder, deny ? UnixFileMode.UserRead | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void ProfileRecordFormat()
    {
        const string requested = @"C:\Users\Some One\AppData\Local\MatrixDesktop\WebView2";
        const string effective = @"D:\gate run\artifacts\gate\webview-profile";
        var record = HostStorage.ProfileRecord(requested, effective);
        // The gate-ci contract: 'WebView2 profile requested=<path> effective=<path>'.
        var match = Regex.Match(record, "^WebView2 profile requested=(?<requested>.*) effective=(?<effective>.*)$");
        Check.True(match.Success, $"the record must match the gate's pattern: {record}");
        Check.Equal(requested, match.Groups["requested"].Value, "the requested folder must round-trip, spaces included");
        Check.Equal(effective, match.Groups["effective"].Value, "the effective folder must round-trip, spaces included");
    }
}
