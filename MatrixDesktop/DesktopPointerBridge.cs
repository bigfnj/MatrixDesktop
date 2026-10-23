using System.Drawing;
using System.Runtime.InteropServices;

namespace MatrixDesktop;

internal sealed class DesktopPointerBridge : IDisposable
{
    internal sealed record Pointer(string EventType, Point ScreenPoint, int Button, int DeltaX, int DeltaY);
    private delegate nint MouseProc(int code, nint message, nint data);
    private readonly MouseProc _callback;
    private readonly Action<Pointer> _post;
    private nint _hook;
    private long _lastMove;
    private bool _reportedFailure;

    internal DesktopPointerBridge(Action<Pointer> post) { _post = post; _callback = OnMouse; }

    internal void SetEnabled(bool enabled)
    {
        if (!enabled) { Dispose(); return; }
        if (_hook != 0 || _reportedFailure) return;
        _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
        if (_hook == 0)
        {
            _reportedFailure = true;
            Shared.Logger.Warn("Desktop pointer input is unavailable; the mouse hook could not be installed.");
        }
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0 && DesktopWindowHost.DesktopHasForeground())
        {
            var mouse = Marshal.PtrToStructure<MouseData>(data);
            var kind = (int)message;
            var move = kind == 0x0200;
            var now = Environment.TickCount64;
            if (!move || now - _lastMove >= 30)
            {
                _lastMove = now;
                var delta = unchecked((short)(mouse.Data >> 16));
                var type = kind switch { 0x0200 => "move", 0x0201 or 0x0204 or 0x0207 => "down", 0x0202 or 0x0205 or 0x0208 => "up", 0x020a or 0x020e => "wheel", _ => "" };
                var button = kind is 0x0204 or 0x0205 ? 2 : kind is 0x0207 or 0x0208 ? 1 : 0;
                if (type.Length > 0) _post(new Pointer(type, mouse.Point, button, kind == 0x020e ? delta : 0, kind == 0x020a ? -delta : 0));
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public Point Point; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, MouseProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
