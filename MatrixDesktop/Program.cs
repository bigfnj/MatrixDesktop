using System;
using System.Windows.Forms;

namespace MatrixDesktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Prefer per-monitor DPI awareness so the WebView stays crisp across mixed-DPI displays.
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

        if (AppCli.IsHelpRequested(args))
        {
            MessageBox.Show(
                AppCli.GetHelpText(),
                "MatrixDesktop - Help",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.Run(new MainForm(args));
    }
}
