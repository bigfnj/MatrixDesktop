using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MatrixDesktop;

/// <summary>
/// Exits the app on non-injected keyboard input.
///
/// Why a low-level keyboard hook (WH_KEYBOARD_LL)?
/// - It continues to work even when focus is inside native child HWNDs (e.g., WebView2).
/// - It provides injection flags (LLKHF_INJECTED / LLKHF_LOWER_IL_INJECTED), allowing us
///   to ignore typical software-injected input created via SendInput/keybd_event.
///
/// Notes / limitations:
/// - If a tool injects input via a virtual HID keyboard device/driver, Windows typically
///   treats it as "real" hardware and the injected flags may NOT be set. There is no
///   universal, reliable way to distinguish those from a physical keyboard in user mode.
/// </summary>
internal sealed class LowLevelKeyboardExit : IDisposable
{
    private readonly Form _owner;
    private readonly bool _exitOnEsc;
    private readonly bool _exitOnAnyKey;
    private readonly bool _globalKeyExit;

    private bool _disposed;
    private bool _closeRequested;
    private IntPtr _hookId = IntPtr.Zero;

    // Keep the delegate alive so the GC does not collect it while
    // the native hook still references the callback thunk.
    private HookProc? _hookProc;

    private const int WH_KEYBOARD_LL = 13;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const uint LLKHF_LOWER_IL_INJECTED = 0x00000002;
    private const uint LLKHF_INJECTED = 0x00000010;

    private static readonly uint CurrentPid = (uint)Process.GetCurrentProcess().Id;

    public LowLevelKeyboardExit(Form owner, bool exitOnEsc, bool exitOnAnyKey, bool globalKeyExit)
    {
        _owner = owner;
        _exitOnEsc = exitOnEsc;
        _exitOnAnyKey = exitOnAnyKey;
        _globalKeyExit = globalKeyExit;
    }

    public void Uninstall()
    {
        if (_hookId == IntPtr.Zero) return;
        
        try
        {
            UnhookWindowsHookEx(_hookId);
        }
        catch
        {
            // Ignore.
        }
        _hookId = IntPtr.Zero;
    }

    public void Install()
    {
        if (_disposed) return;
        if (_hookId != IntPtr.Zero) return;

        // If exit is fully disabled, don't hook.
        if (!_exitOnEsc && !_exitOnAnyKey) return;

        _hookProc = HookCallback;

        try
        {
            // For WH_KEYBOARD_LL, the hook proc can live in this EXE.
            var module = GetModuleHandle(null);
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, module, 0);

            // Fallback: some environments are picky about module handles; WH_KEYBOARD_LL
            // can also work with a null module handle because the callback lives in-process.
            if (_hookId == IntPtr.Zero)
            {
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
            }

            // Diagnostic: log whether the hook was installed successfully.
            if (_hookId == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[LowLevelKeyboardExit] SetWindowsHookEx FAILED — Win32 error: {err}");
            }
            else
            {
                Debug.WriteLine($"[LowLevelKeyboardExit] Hook installed successfully (hookId=0x{_hookId:X})");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LowLevelKeyboardExit] Install exception: {ex.Message}");
            // Best-effort: if hooking fails, we just won't support key-exit.
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (!_disposed && nCode >= 0 && (_exitOnEsc || _exitOnAnyKey))
            {
                var msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                    // Ignore typical software-injected input.
                    if (IsInjected(data.flags))
                    {
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // Unless explicitly enabled, only exit when our window is foreground.
                    if (!_globalKeyExit && !IsOurProcessForeground())
                    {
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    var vk = data.vkCode;
                    if (vk is 0 or 0xFF)
                    {
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    var key = (Keys)vk;

                    if (_exitOnAnyKey)
                    {
                        RequestClose();
                    }
                    else if (_exitOnEsc && key == Keys.Escape)
                    {
                        RequestClose();
                    }
                }
            }
        }
        catch
        {
            // Never let the hook throw.
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool IsInjected(uint flags)
    {
        return (flags & LLKHF_INJECTED) != 0 || (flags & LLKHF_LOWER_IL_INJECTED) != 0;
    }

    private static bool IsOurProcessForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            return pid == CurrentPid;
        }
        catch
        {
            return false;
        }
    }

    private void RequestClose()
    {
        if (_closeRequested) return;
        _closeRequested = true;

        try
        {
            if (_owner.IsDisposed || _owner.Disposing) return;

            // Defer to avoid re-entrancy during hook callback.
            _owner.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!_owner.IsDisposed && !_owner.Disposing)
                    {
                        _owner.Close();
                    }
                }
                catch
                {
                    // Ignore.
                }
            }));
        }
        catch
        {
            try
            {
                if (!_owner.IsDisposed && !_owner.Disposing)
                {
                    _owner.Close();
                }
            }
            catch
            {
                // Ignore.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
        catch
        {
            // Ignore.
        }

        _hookProc = null;
    }

    // ── P/Invoke declarations ────────────────────────────────────────────────
    //
    // All DllImport (not LibraryImport) to avoid source-generator subtleties
    // with callback delegates and to match the proven v0 implementation exactly.

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
