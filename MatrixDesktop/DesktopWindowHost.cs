using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace MatrixDesktop;

internal static class DesktopWindowHost
{
    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long WsChild = 0x40000000, WsPopup = 0x80000000;
    private const long WsCaption = 0x00c00000, WsThickFrame = 0x00040000;
    private const long WsExAppWindow = 0x00040000, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    private delegate bool EnumWindowsProc(nint window, nint parameter);

    internal static nint FindWorker()
    {
        var progman = FindWindow("Progman", null);
        if (progman == 0) return 0;
        // A fresh modern Explorer requires the explicit raised-desktop request.
        return DesktopHostDiscovery.Ensure(() => FindExistingWorker(progman),
            (wParam, lParam) => SendMessageTimeout(progman, 0x052c, wParam, lParam, 2, 1000, out _));
    }

    private static nint FindExistingWorker(nint progman)
    {
        // Current Windows 11 can place the wallpaper WorkerW inside Progman,
        // beside its icon host, rather than creating a top-level sibling.
        var nested = FindWindowEx(progman, 0, "WorkerW", null);
        if (nested != 0 && FindWindowEx(nested, 0, "SHELLDLL_DefView", null) == 0
            && FindWindowEx(progman, 0, "SHELLDLL_DefView", null) != 0) return nested;
        nint worker = 0;
        EnumWindows((window, _) =>
        {
            var icons = FindWindowEx(window, 0, "SHELLDLL_DefView", null);
            if (icons == 0 || FindWindowEx(icons, 0, "SysListView32", null) == 0) return true;
            var behind = FindWindowEx(0, window, "WorkerW", null);
            if (behind != 0) worker = behind;
            return worker == 0;
        }, 0);
        return worker;
    }

    internal static bool IsUsable(nint window) => window != 0 && IsWindow(window);
    internal static nint ParentOf(nint window) => GetParent(window);
    internal static bool DesktopHasForeground()
    {
        var foreground = GetForegroundWindow();
        if (ClassOf(foreground) is not ("Progman" or "WorkerW" or "SHELLDLL_DefView")) return false;
        var progman = FindWindow("Progman", null);
        if (progman == 0) return false;
        GetWindowThreadProcessId(progman, out var shellProcess);
        GetWindowThreadProcessId(foreground, out var foregroundProcess);
        return shellProcess != 0 && shellProcess == foregroundProcess;
    }
    internal static string ClassOf(nint window)
    {
        var name = new StringBuilder(256);
        _ = GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    internal static Rectangle ClientBounds(nint parent)
    {
        if (!GetClientRect(parent, out var rect)) throw new Win32Exception();
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    internal static void Attach(nint child, nint parent, Rectangle screenBounds, bool preview = false)
    {
        if (!IsUsable(parent)) throw new InvalidOperationException("The desktop host is unavailable.");
        var style = GetWindowLongPtr(child, GwlStyle).ToInt64();
        SetWindowLongPtr(child, GwlStyle, new nint((style | WsChild) & ~WsPopup & ~WsCaption & ~WsThickFrame));
        var ex = GetWindowLongPtr(child, GwlExStyle).ToInt64();
        SetWindowLongPtr(child, GwlExStyle, new nint((ex | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow));
        Marshal.SetLastPInvokeError(0);
        var oldParent = SetParent(child, parent);
        var error = Marshal.GetLastPInvokeError();
        if (oldParent == 0 && error != 0) throw new Win32Exception(error, "Could not attach the renderer to the desktop host.");
        var origin = new Point();
        if (!ClientToScreen(parent, ref origin)) throw new Win32Exception();
        var bounds = preview ? ClientBounds(parent) : new Rectangle(screenBounds.X - origin.X, screenBounds.Y - origin.Y, screenBounds.Width, screenBounds.Height);
        if (!SetWindowPos(child, new nint(1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0020 | 0x0040))
            throw new Win32Exception();
        _ = EnableWindow(child, false);
        if (GetParent(child) != parent) throw new InvalidOperationException("The desktop parent did not persist.");
    }

    internal static bool ForegroundCoversAnyMonitor(IEnumerable<Rectangle> monitors)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0 || IsIconic(foreground)) return false;
        var name = ClassOf(foreground);
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!GetWindowRect(foreground, out var rect)) return false;
        return PlaybackPolicy.CoversAnyMonitor(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom), monitors);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] private static extern nint GetParent(nint child);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
