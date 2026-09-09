using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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

    // Per-process cap, so one bad frame cannot write dumps until the disk fills.
    private const int MaxDumpsPerProcess = 3;

    // Folder cap across all runs, oldest pruned first.
    private const int MaxDumpsRetained = 10;

    private static int _dumpsWritten;

    private static bool TryReserveDumpSlot()
        => System.Threading.Interlocked.Increment(ref _dumpsWritten) <= MaxDumpsPerProcess;

    private static void PruneOldDumps(string dumpDir)
    {
        try
        {
            var existing = new DirectoryInfo(dumpDir)
                .GetFiles("*.dmp")
                .OrderByDescending(static file => file.LastWriteTimeUtc)
                .Skip(MaxDumpsRetained - 1)
                .ToArray();

            foreach (var stale in existing)
            {
                try
                {
                    stale.Delete();
                }
                catch
                {
                    // A dump still open in a debugger is not worth failing over.
                }
            }

            if (existing.Length > 0)
            {
                Logger.Info($"Pruned {existing.Length} old crash dump(s) from '{dumpDir}'.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not prune old crash dumps: {ex.Message}");
        }
    }

    public static void Install(string processLabel)
    {
        if (_installed) return;
        _installed = true;
        _processLabel = string.IsNullOrWhiteSpace(processLabel) ? AppName : processLabel;

        // CatchException routes exceptions escaping the WinForms message pump to
        // Application.ThreadException, which is subscribed below. That is deliberate and it
        // is the opposite of what a previous version of this comment claimed: with
        // ThrowException, WinForms would NOT raise ThreadException and these handlers would
        // never see a message-pump exception at all.
        //
        // Consequence worth knowing: after writing a dump and showing the dialog, the
        // process keeps running. That is intentional for a visualiser, where a failed
        // render pass should not take the window down, but it does mean a repeating
        // exception would keep producing dumps, which is why WriteDump caps them.
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

            // The log rotates at 2 MB but nothing ever bounded this folder, and because the
            // app deliberately keeps running after a fatal exception, a repeating fault
            // could fill the disk one ~10 MB dump at a time.
            if (!TryReserveDumpSlot())
            {
                Logger.Warn($"Dump suppressed: {MaxDumpsPerProcess} already written by this process. Reason='{reason}'.");
                return null;
            }

            PruneOldDumps(dumpDir);

            // Milliseconds, not just seconds. Two faults inside the same second produced an
            // identical filename and the second dump silently overwrote the first, which
            // was observed while verifying the retention cap.
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss.fff", CultureInfo.InvariantCulture);
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
