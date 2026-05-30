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
        Application.Run(new ConfiguratorForm());
    }

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
