using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MatrixDesktop;

/// <summary>
/// Best-effort helpers to make the app the foreground window.
///
/// Windows intentionally restricts "focus stealing" in some scenarios (e.g., when the user is
/// actively interacting with another process). This helper uses a few common, low-risk tactics:
/// - WinForms Activate/BringToFront
/// - temporary TopMost bump (restored immediately to the desired state)
/// - ordinary SetForegroundWindow, without joining another process's input queue
///
/// This is not a 100% guarantee (by design, Windows can still refuse), but it greatly improves
/// reliability for typical launcher flows.
/// </summary>
internal static class ForegroundWindow
{
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private static readonly uint CurrentPid = (uint)Environment.ProcessId;

    public static void BestEffortBringToFront(Form form, bool desiredTopMost)
    {
        if (form is null) return;
        if (form.IsDisposed || form.Disposing) return;

        try
        {
            // Ensure we have a handle.
            var hwnd = form.Handle;
            if (hwnd == IntPtr.Zero) return;

            // If minimized, restore.
            try
            {
                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SW_RESTORE);
                }
            }
            catch
            {
                // Ignore.
            }

            // Ensure shown.
            try
            {
                ShowWindow(hwnd, SW_SHOW);
            }
            catch
            {
                // Ignore.
            }

            // WinForms-level activation.
            try
            {
                form.Show();
                form.BringToFront();
                form.Activate();
            }
            catch
            {
                // Ignore.
            }

            // A brief TopMost "bump" helps in many cases without leaving the window always-on-top.
            try
            {
                form.TopMost = true;
                form.TopMost = desiredTopMost;
            }
            catch
            {
                // Ignore.
            }

            // Native attempt (may still be refused by OS policy in some cases).
            try
            {
                ForceForegroundWindow(hwnd);
            }
            catch
            {
                // Ignore.
            }
        }
        catch
        {
            // Ignore.
        }
    }

    public static bool IsThisProcessForeground()
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

    private static void ForceForegroundWindow(IntPtr hwnd)
    {
        // Attaching a foreign input queue makes activation synchronous with that
        // thread. A stalled foreground queue can then freeze our UI and prevent
        // screensaver input-exit timers from running. Respect Windows' focus
        // policy and keep this request independent of foreign message pumps.
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        SetActiveWindow(hwnd);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
