using System.Runtime.InteropServices;
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
        ("Desktop shell class check reads real window classes without a shell session", ShellClassReadsRealClasses),
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
        // Message-only windows of classes registered here, so the check needs no shell
        // session (the tier-1 gate runs this suite on a hosted runner too).
        foreach (var (className, accepted) in new[] { ("Progman", true), ("WorkerW", true), ("SHELLDLL_DefView", true), ("Shell_TrayWnd", false), ("Worker", false), ("WorkerWx", false) })
        {
            var instance = GetModuleHandleW(null);
            var windowClass = new WndClass { WndProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"), Instance = instance, ClassName = className };
            var registered = RegisterClassW(ref windowClass) != 0;
            if (!registered && Marshal.GetLastWin32Error() != 1410) throw new InvalidOperationException($"Could not register the {className} test class: {Marshal.GetLastWin32Error()}.");
            var window = CreateWindowExW(0, className, null, 0, 0, 0, 0, 0, -3, 0, instance, 0);
            try
            {
                Require(window != 0, $"Could not create a {className} message-only window: {Marshal.GetLastWin32Error()}.");
                Require(DesktopWindowHost.ClassOf(window) == className, $"Control: the string class read must name {className}.");
                Require(DesktopWindowHost.IsShellClass(window) == accepted, $"The in-place class check must {(accepted ? "accept" : "reject")} {className}.");
            }
            finally
            {
                if (window != 0) DestroyWindow(window);
                if (registered) UnregisterClassW(className, instance);
            }
        }
        Require(!DesktopWindowHost.IsShellClass(0), "The in-place class check must reject a missing window.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass { public uint Style; public nint WndProc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WndClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int exStyle, string className, string? title, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern nint GetProcAddress(nint module, string name);
}
