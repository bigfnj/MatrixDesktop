using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MatrixDesktop.Shared;

// Cross-project crash diagnostics. Registers handlers for the three "escaped
// exception" sources WinForms / .NET expose (AppDomain, WinForms message pump,
// and TaskScheduler), then on any unhandled exception writes:
//   1. A MiniDumpWriteDump-format .dmp under %LOCALAPPDATA%\MatrixDesktop\dumps\
//   2. An ERROR-level log line via the existing Shared.Logger
//   3. A single MessageBox showing the dump path so the user can attach it
//
// Why MiniDumpNormal (not WithFullMemory): keeps dumps under ~10MB so they're
// emailable. If you ever need richer dumps for hard-to-repro crashes, bump
// DumpType to a value with MiniDumpWithFullMemory bits set.
internal static class CrashDumpWriter
{
    private const string AppName = "MatrixDesktop";
    private const int MiniDumpNormal = 0;

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        SafeHandle hFile,
        int dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    private static string _processLabel = AppName;
    private static volatile bool _installed;

    public static void Install(string processLabel)
    {
        if (_installed) return;
        _installed = true;
        _processLabel = string.IsNullOrWhiteSpace(processLabel) ? AppName : processLabel;

        // Ensure CLR-internal unhandled exceptions go through our handler too.
        // Without ThrowException, WinForms swallows ThreadException before
        // AppDomain.UnhandledException ever fires.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Logger.Info($"CrashDumpWriter installed for process '{_processLabel}'.");
    }

    private static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
    {
        HandleFatal("Application.ThreadException", e.Exception);
    }

    private static void OnAppDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        HandleFatal(
            e.IsTerminating ? "AppDomain.UnhandledException (terminating)" : "AppDomain.UnhandledException",
            e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        HandleFatal("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void HandleFatal(string source, Exception? ex)
    {
        string? dumpPath = null;

        try
        {
            dumpPath = WriteDump(source);
        }
        catch (Exception writeEx)
        {
            // Logger swallows internally — never let the dump path crash the
            // handler, that would defeat the whole purpose.
            try { Logger.Error($"CrashDumpWriter: failed to write dump for '{source}'.", writeEx); }
            catch { /* ignore */ }
        }

        try
        {
            Logger.Error($"Unhandled exception via {source}. Dump='{dumpPath ?? "(not written)"}'.", ex);
        }
        catch { /* ignore — logging may already be impaired */ }

        try
        {
            var message = ex?.Message ?? "Unknown error.";
            var detail = dumpPath is null
                ? $"{_processLabel} crashed.\n\n{message}"
                : $"{_processLabel} crashed.\n\n{message}\n\nDiagnostic dump:\n{dumpPath}";

            MessageBox.Show(detail, _processLabel, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* dialog itself can fail in catastrophic states */ }
    }

    public static string? WriteDump(string reason)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Path.GetTempPath();
            }

            var dumpDir = Path.Combine(localAppData, AppName, "dumps");
            Directory.CreateDirectory(dumpDir);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(dumpDir, $"{_processLabel}-{stamp}-pid{Environment.ProcessId}.dmp");

            using var process = Process.GetCurrentProcess();
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            var ok = MiniDumpWriteDump(
                process.Handle,
                (uint)process.Id,
                stream.SafeFileHandle,
                MiniDumpNormal,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                Logger.Warn($"MiniDumpWriteDump returned false. Win32 error={err}. Reason='{reason}'.");
                return null;
            }

            Logger.Info($"Crash dump written. Path='{path}' Reason='{reason}'.");
            return path;
        }
        catch (Exception ex)
        {
            try { Logger.Error($"WriteDump exception. Reason='{reason}'.", ex); }
            catch { /* ignore */ }
            return null;
        }
    }
}
