using MatrixDesktop;
using System.Windows.Forms;

internal static class HostLifecycleTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Native host detaches a dead controller before disposal", DetachBeforeDispose),
        ("Native host creates modern WorkerW before bounded legacy fallback", WorkerRequests),
        ("Native host loop ends a context that exited while it was constructed", ConstructorExit),
        ("Native host attaches a newer surface above an older one in the same desktop host", AttachOrder),
    ];

    // AUD-72. Attach used HWND_BOTTOM, so a newer wallpaper surface went under an older one in
    // the same WorkerW and its new settings never showed. Test-owned hidden windows only.
    private static void AttachOrder() => Require(ReturnsWithin(() =>
    {
        using var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000), Size = new System.Drawing.Size(320, 240) };
        using var older = new Form { TopLevel = false, FormBorderStyle = FormBorderStyle.None };
        using var newer = new Form { TopLevel = false, FormBorderStyle = FormBorderStyle.None };
        var bounds = new System.Drawing.Rectangle(0, 0, 320, 240);
        DesktopWindowHost.Attach(older.Handle, host.Handle, bounds);
        DesktopWindowHost.Attach(newer.Handle, host.Handle, bounds);
        Require(DesktopWindowHost.ParentOf(older.Handle) == host.Handle && DesktopWindowHost.ParentOf(newer.Handle) == host.Handle, "witness: both surfaces must be attached to the host");
        Require(GetWindow(host.Handle, 5) == newer.Handle, "A newer surface must attach above an older one in the same desktop host.");
        Require(GetWindow(newer.Handle, 2) == older.Handle, "The older surface must stay attached directly beneath the newer one.");
    }, 10000), "The attach-order check must finish.");

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    // AUD-04. MatrixApplicationContext can end inside its constructor (a preview parent that is
    // already gone, a renderer that cannot be created); Application.Run then pumped forever.
    private static void ConstructorExit()
    {
        Require(!ReturnsWithin(() => Application.Run(new SelfEndingContext(endWhileConstructing: true)), 1000),
            "Control: plain Application.Run must keep pumping for a context that ended in its constructor, or this test cannot see the hang.");
        SelfEndingContext? ended = null;
        Require(ReturnsWithin(() => { ended = new SelfEndingContext(endWhileConstructing: true); HostLoop.Run(ended, () => ended.Exited); }, 5000),
            "A context that ended while it was constructed must not leave the message loop pumping.");
        SelfEndingContext? live = null;
        Require(ReturnsWithin(() => { live = new SelfEndingContext(endWhileConstructing: false); HostLoop.Run(live, () => live.Exited); }, 5000) && live!.Exited,
            "A live context must still run its loop until it ends itself from inside that loop.");
    }

    // Runs body on its own STA thread. A thread still pumping at the deadline is ended with
    // WM_QUIT so the harness itself can exit.
    private static bool ReturnsWithin(Action body, int timeoutMs)
    {
        uint nativeThread = 0;
        Exception? failure = null;
        using var started = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            nativeThread = GetCurrentThreadId();
            started.Set();
            try { body(); } catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        started.Wait();
        var returned = thread.Join(timeoutMs);
        if (!returned) { PostThreadMessage(nativeThread, 0x0012, 0, 0); thread.Join(5000); }
        if (failure is not null) throw new InvalidOperationException("The loop body threw: " + failure.Message, failure);
        return returned;
    }

    private sealed class SelfEndingContext : ApplicationContext
    {
        internal bool Exited { get; private set; }
        internal SelfEndingContext(bool endWhileConstructing)
        {
            if (endWhileConstructing) { ExitThread(); return; }
            var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) => { timer.Dispose(); ExitThread(); };
            timer.Start();
        }
        protected override void ExitThreadCore() { Exited = true; base.ExitThreadCore(); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, int message, nint wParam, nint lParam);

    private static void DetachBeforeDispose()
    {
        var owner = new Control();
        var child = new DeadControllerControl();
        owner.Controls.Add(child);
        var warnings = 0;
        HostControlLifetime.DetachAndDispose(child, owner.Controls, _ => warnings++);
        Require(child.Attempts == 1 && warnings == 1, "A destroyed native controller must be disposed once and reported.");
        Require(!owner.Controls.Contains(child), "The failed control must be detached before its dispose is attempted.");
        owner.Dispose();
        Require(owner.IsDisposed && child.Attempts == 1, "Owner disposal must not retry the failed native controller.");
        var unexpected = new UnexpectedFailureControl();
        var other = new Control(); other.Controls.Add(unexpected);
        try { HostControlLifetime.DetachAndDispose(unexpected, other.Controls, _ => throw new InvalidOperationException("Unexpected failures cannot be swallowed.")); }
        catch (ArgumentException) { other.Dispose(); return; }
        throw new InvalidOperationException("An unrelated application error must propagate.");
    }

    private static void WorkerRequests()
    {
        var requests = new List<(nuint, nint)>();
        Require(DesktopHostDiscovery.Ensure(() => 42, (w, l) => requests.Add((w, l))) == 42 && requests.Count == 0,
            "An existing desktop host must not receive a creation request.");
        nint worker = 0;
        Require(DesktopHostDiscovery.Ensure(() => worker, (w, l) => { requests.Add((w, l)); if (w == 0xD && l == 1) worker = 64; }) == 64,
            "Fresh Explorer must receive the modern 0xD/1 desktop creation request.");
        Require(requests.SequenceEqual(new[] { ((nuint)0xD, (nint)1) }), "The modern request must complete without an extra legacy message.");
        requests.Clear(); worker = 0;
        Require(DesktopHostDiscovery.Ensure(() => worker, (w, l) => { requests.Add((w, l)); if (w == 0 && l == 0) worker = 84; }) == 84,
            "A legacy desktop must still be discovered after the modern request fails.");
        Require(requests.SequenceEqual(new[] { ((nuint)0xD, (nint)1), ((nuint)0, (nint)0) }), "Modern then legacy must be the exact fallback order.");
        requests.Clear();
        Require(DesktopHostDiscovery.Ensure(() => 0, (w, l) => requests.Add((w, l))) == 0 && requests.Count == 2,
            "An unavailable shell must fail explicitly after two bounded requests.");
    }

    private sealed class DeadControllerControl : Control
    {
        internal int Attempts { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(false); return; }
            Attempts++; throw new InvalidOperationException("Injected native controller already closed.");
        }
    }
    private sealed class UnexpectedFailureControl : Control
    {
        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(false); return; }
            throw new ArgumentException("Injected unrelated failure.");
        }
    }
}
