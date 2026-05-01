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
/// - SetForegroundWindow with a brief AttachThreadInput to the current foreground thread
///
/// This is not a 100% guarantee (by design, Windows can still refuse), but it greatly improves
/// reliability for typical launcher flows.
/// </summary>
internal static partial class ForegroundWindow
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
        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();

        if (foreground != IntPtr.Zero)
        {
            var foregroundThread = GetWindowThreadProcessId(foreground, out _);

            // Temporarily attach our input processing to the thread that owns the current
            // foreground window. This increases the chance SetForegroundWindow succeeds.
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                AttachThreadInput(foregroundThread, currentThread, true);
                try
                {
                    SetForegroundWindow(hwnd);
                    BringWindowToTop(hwnd);
                    SetActiveWindow(hwnd);
                }
                finally
                {
                    AttachThreadInput(foregroundThread, currentThread, false);
                }

                return;
            }
        }

        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        SetActiveWindow(hwnd);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetActiveWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
