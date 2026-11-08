using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MatrixDesktopConfigurator;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Crash dumps + unhandled exception triage. Must run first so even an
        // early-startup failure gets captured.
        MatrixDesktop.Shared.CrashDumpWriter.Install("MatrixDesktopConfigurator");

        TrySetAppUserModelId("BigFnJ.MatrixDesktop.Configurator");

        // One configurator per preset store (SingleInstanceGuard.cs). A refused launch hands
        // the user to the window that owns the store and exits 0 without touching the store.
        // The guard stays referenced until Run returns.
        var storage = new StorageService();
        using var guard = SingleInstanceGuard.TryAcquire(storage.StoragePath);
        if (guard is null)
        {
            ActivateOwningWindow(SingleInstanceGuard.ObjectName(storage.StoragePath));
            return;
        }

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ConfiguratorForm(storage));
    }

    // The owning configurator tags its main window with the store's object name. Restore it
    // only when minimized (SW_RESTORE would also un-maximize a maximized window), then ask
    // for the foreground; this freshly started process normally holds that right.
    private static void ActivateOwningWindow(string windowTag)
    {
        try
        {
            var target = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                if (GetProp(hwnd, windowTag) == IntPtr.Zero) return true;
                target = hwnd;
                return false;
            }, IntPtr.Zero);

            if (target == IntPtr.Zero)
            {
                return;
            }

            if (IsIconic(target))
            {
                ShowWindow(target, SwRestore);
            }

            SetForegroundWindow(target);
        }
        catch
        {
            // Activation is a courtesy; the refusal is what protects the store.
        }
    }

    private const int SwRestore = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hwnd, string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private static void TrySetAppUserModelId(string appId)
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(appId);
        }
        catch
        {
            // Taskbar grouping/icon identity is cosmetic; keep startup resilient.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
