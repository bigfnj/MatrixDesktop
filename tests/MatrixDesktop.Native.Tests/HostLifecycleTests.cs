using MatrixDesktop;
using System.Windows.Forms;

internal static class HostLifecycleTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Native host detaches a dead controller before disposal", DetachBeforeDispose),
        ("Native host creates modern WorkerW before bounded legacy fallback", WorkerRequests),
    ];

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

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
