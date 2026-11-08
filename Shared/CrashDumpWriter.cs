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

    // DbgHelp is single-threaded and MiniDumpWriteDump callers must serialise it, yet the UI
    // thread, the finalizer thread (unobserved tasks) and any thread with an unhandled
    // exception can all reach the writer at once; the reservation counter is a cap, not a
    // lock. SemaphoreSlim is not reentrant, and the wait is bounded so a writer that hangs
    // cannot stop another thread's report. Logging and dialogs stay outside it.
    private static readonly System.Threading.SemaphoreSlim NativeWriteGuard = new(1, 1);
    private const int NativeWriteWaitMs = 10_000;

    private static bool TryReserveDumpSlot()
    {
        if (System.Threading.Interlocked.Increment(ref _dumpsWritten) <= MaxDumpsPerProcess) return true;
        System.Threading.Interlocked.Decrement(ref _dumpsWritten);
        return false;
    }

    // Reserving is not the same as succeeding. Without this, a dump that failed to write
    // still burned a slot, so three failures in a row (insufficient privilege, a full disk,
    // AV interception, all of which cluster) permanently disabled dumps while the log
    // claimed three had been written.
    private static void ReleaseDumpSlot()
        => System.Threading.Interlocked.Decrement(ref _dumpsWritten);

    // A failed attempt leaves the empty or partial file it created. Left in place it is newer
    // than every retained dump, so the next prune would evict a real one in its favour.
    // Returns a warning for the caller to log once the guard is released, or null.
    private static string? DeleteFailedDump(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not delete the failed crash dump '{path}': {ex.Message}";
        }
    }

    // Milliseconds, not just seconds. Two faults inside the same second produced an
    // identical filename and the second dump silently overwrote the first, which
    // was observed while verifying the retention cap. A millisecond can repeat too, so a
    // name already on disk gets a numeric suffix. Called inside the guard, so no other
    // attempt in this process can take the name first, and the pid keeps processes apart.
    private static string ChooseDumpPath(string dumpDir, DateTime now)
    {
        const int maxSuffix = 100;
        var stamp = now.ToString("yyyyMMdd-HHmmss.fff", CultureInfo.InvariantCulture);
        var stem = Path.Combine(dumpDir, $"{_processLabel}-{stamp}-pid{Environment.ProcessId}");
        var path = stem + ".dmp";
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            if (suffix > maxSuffix) throw new IOException($"No unused crash dump name for '{stem}'.");
            path = $"{stem}-{suffix}.dmp";
        }
        return path;
    }

    // Runs only after a dump is on disk, so a failed attempt can never evict a retained one,
    // and never ranks the new dump itself: one written after the clock was set back would
    // otherwise sort below dumps with later timestamps and be deleted at once.
    private static void PruneOldDumps(string dumpDir, string newDumpPath)
    {
        try
        {
            var newDumpName = Path.GetFileName(newDumpPath);
            var existing = new DirectoryInfo(dumpDir)
                .GetFiles("*.dmp")
                .Where(file => !string.Equals(file.Name, newDumpName, StringComparison.OrdinalIgnoreCase))
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
        => WriteDumpWithBackend(reason, ResolveDumpDirectory, WriteNativeDump);

    private static string ResolveDumpDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Path.GetTempPath();
        return Path.Combine(localAppData, AppName, "dumps");
    }

    // The backend seam keeps reservation failures testable without writing a
    // process dump or touching a user's diagnostics directory.
    // clock and writerWait exist for tests: same-instant names and a short bounded wait.
    internal static string? WriteDumpWithBackend(string reason, Func<string> resolveDirectory, Func<string, bool> writeDump,
        Func<DateTime>? clock = null, TimeSpan? writerWait = null)
    {
        var reserved = false;
        try
        {
            var dumpDir = resolveDirectory();
            Directory.CreateDirectory(dumpDir);

            // The log rotates at 2 MB but nothing ever bounded this folder, and because the
            // app deliberately keeps running after a fatal exception, a repeating fault
            // could fill the disk one ~10 MB dump at a time.
            if (!TryReserveDumpSlot())
            {
                Logger.Warn($"Dump suppressed: {MaxDumpsPerProcess} already written by this process. Reason='{reason}'.");
                return null;
            }
            reserved = true;

            if (!NativeWriteGuard.Wait(writerWait ?? TimeSpan.FromMilliseconds(NativeWriteWaitMs)))
            {
                ReleaseDumpSlot();
                reserved = false;
                Logger.Warn($"Dump skipped: another thread was still writing a crash dump. Reason='{reason}'.");
                return null;
            }

            string path;
            var ok = false;
            var err = 0;
            Exception? writeError = null;
            string? cleanupWarning = null;
            try
            {
                path = ChooseDumpPath(dumpDir, clock?.Invoke() ?? DateTime.Now);
                try
                {
                    ok = writeDump(path);
                    if (!ok) err = Marshal.GetLastWin32Error();
                }
                catch (Exception ex)
                {
                    writeError = ex;
                }

                // Inside the guard: once it is released, another attempt in the same
                // millisecond may choose this name again.
                if (!ok) cleanupWarning = DeleteFailedDump(path);
            }
            finally
            {
                NativeWriteGuard.Release();
            }

            if (cleanupWarning is not null) Logger.Warn(cleanupWarning);
            if (!ok)
            {
                ReleaseDumpSlot();
                reserved = false;
                if (writeError is null) Logger.Warn($"MiniDumpWriteDump returned false. Win32 error={err}. Reason='{reason}'.");
                else Logger.Error($"WriteDump exception. Reason='{reason}'.", writeError);
                return null;
            }

            Logger.Info($"Crash dump written. Path='{path}' Reason='{reason}'.");
            PruneOldDumps(dumpDir, path);
            return path;
        }
        catch (Exception ex)
        {
            if (reserved) ReleaseDumpSlot();
            try { Logger.Error($"WriteDump exception. Reason='{reason}'.", ex); }
            catch { /* ignore */ }
            return null;
        }
    }

    private static bool WriteNativeDump(string path)
    {
        bool written;
        int error;
        using (var process = Process.GetCurrentProcess())
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            written = MiniDumpWriteDump(process.Handle, (uint)process.Id, stream.SafeFileHandle,
                MiniDumpNormal, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            error = Marshal.GetLastPInvokeError();
        }
        // Closing the file/process handles must not replace the native failure
        // code that the caller reports.
        Marshal.SetLastPInvokeError(error);
        return written;
    }
}
