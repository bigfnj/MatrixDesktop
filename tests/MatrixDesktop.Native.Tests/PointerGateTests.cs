using MatrixDesktop;

// The wallpaper pointer gate with an injected foreground probe, plus the real probe's
// per-call allocation (it runs inside a global mouse hook).
internal static class PointerGateTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Pointer gate throttles moves before probing the foreground", ThrottleBeforeProbe),
        ("Pointer gate forwards the same events as before", ForwardedSequence),
        ("Desktop foreground probe allocates nothing per call", ProbeAllocatesNothing),
        ("Desktop shell class check reads real window classes", ShellClassReadsRealClasses),
    ];

    private const int Move = 0x0200, LeftDown = 0x0201;

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void ThrottleBeforeProbe()
    {
        var probes = 0;
        var gate = new PointerGate(() => { probes++; return false; });
        var forwarded = 0;
        // 100 moves between 1000 and 1029 ms with another application in front.
        for (var i = 0; i < 100; i++) if (gate.ShouldForward(Move, 1000 + i * 30 / 100)) forwarded++;
        Require(forwarded == 0 && probes == 1, $"Moves inside one throttle interval must probe once and forward nothing: probes={probes}, forwarded={forwarded}.");
    }

    private static void ForwardedSequence()
    {
        var gate = new PointerGate(() => true);
        (int Message, long Now)[] events = [(Move, 1000), (LeftDown, 1005), (Move, 1010), (Move, 1020), (Move, 1030), (Move, 1040), (Move, 1060)];
        var forwarded = events.Where(e => gate.ShouldForward(e.Message, e.Now)).Select(e => $"{(e.Message == Move ? "move" : "down")}@{e.Now}").ToArray();
        Require(forwarded.SequenceEqual(["move@1000", "down@1005", "move@1040"]),
            $"With the desktop in front the gate must forward move@1000, down@1005, move@1040; got [{string.Join(", ", forwarded)}].");
    }

    private static void ProbeAllocatesNothing()
    {
        for (var i = 0; i < 100; i++) DesktopWindowHost.DesktopHasForeground(); // build the interop stubs first
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) DesktopWindowHost.DesktopHasForeground();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated < 1024, $"The foreground probe must not allocate per call: {allocated} bytes for 1000 calls.");
    }

    private static void ShellClassReadsRealClasses()
    {
        var progman = FindWindow("Progman", null);
        var taskbar = FindWindow("Shell_TrayWnd", null);
        Require(progman != 0 && taskbar != 0, "This check needs the interactive shell's Progman and taskbar windows.");
        Require(DesktopWindowHost.ClassOf(progman) == "Progman", "Control: the string class read must name Progman.");
        Require(DesktopWindowHost.IsShellClass(progman), "The in-place class check must accept the real Progman window.");
        Require(!DesktopWindowHost.IsShellClass(taskbar), "The in-place class check must reject the taskbar (Shell_TrayWnd).");
        Require(!DesktopWindowHost.IsShellClass(0), "The in-place class check must reject a missing window.");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? title);
}
