using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MatrixDesktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Crash dumps + unhandled exception triage. Must run first so even an
        // early-startup failure (DPI mode, AppUserModelId, etc.) gets captured.
        Shared.CrashDumpWriter.Install("MatrixDesktop");

        TrySetAppUserModelId("BigFnJ.MatrixDesktop");

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

        // --help-full / --help-arguments prints the embedded argument guide
        // (the full reference) instead of the short --help summary.
        if (IsFullHelpRequested(args))
        {
            ShowEmbeddedGuide();
            return;
        }

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

    private static bool IsFullHelpRequested(string[] args)
    {
        if (args is null || args.Length == 0) return false;
        return args.Any(a =>
            string.Equals(a, "--help-full", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--help-arguments", StringComparison.OrdinalIgnoreCase));
    }

    private static void ShowEmbeddedGuide()
    {
        string body;
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("MatrixDesktop.ArgumentGuide.txt");
            if (stream is null)
            {
                body = "Embedded argument guide not found.";
            }
            else
            {
                using var reader = new StreamReader(stream);
                body = reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            body = $"Failed to load argument guide: {ex.Message}";
        }

        using var form = new Form
        {
            Text = "MatrixDesktop — Argument Reference",
            Width = 900,
            Height = 700,
            StartPosition = FormStartPosition.CenterScreen,
            MinimumSize = new Size(640, 480),
        };

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9.5f),
            Text = body,
        };

        form.Controls.Add(box);
        form.ShowDialog();
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
