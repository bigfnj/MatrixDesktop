using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace MatrixDesktop.Shared;

// Lightweight synchronous file logger. Both the main app and the configurator
// share this via a <Compile Include="..\Shared\Logger.cs" Link="..."/> reference
// in their .csproj. Synchronous design is intentional: failure-mode diagnostics
// (WebView2 init failure, hook install failure, preset load failure) need to be
// durably on disk before the process possibly exits.
internal static class Logger
{
    private const string AppFolder = "MatrixDesktop";
    private const string LogFileName = "MatrixDesktop.log";
    private const long MaxBytes = 2_000_000;          // ~2MB before rotating to .old
    private const int RotationCheckInterval = 10;     // stat the file every N writes

    private static readonly object _gate = new();
    private static volatile bool _dirCreated;
    private static int _writeCount;

    private static readonly string _logPath = ResolveLogPath();

    public static string LogPath => _logPath;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            EnsureDirectoryExists();

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            sb.Append(" [pid=").Append(Environment.ProcessId);
            sb.Append(" tid=").Append(Environment.CurrentManagedThreadId).Append("] ");
            sb.Append(level).Append(' ').Append(message);

            if (ex != null)
            {
                sb.AppendLine();
                sb.Append(ex);
            }

            sb.AppendLine();

            lock (_gate)
            {
                if (System.Threading.Interlocked.Increment(ref _writeCount) >= RotationCheckInterval)
                {
                    System.Threading.Interlocked.Exchange(ref _writeCount, 0);
                    RotateIfNeeded();
                }

                AppendShared(sb.ToString());
            }
        }
        catch
        {
            // Never let logging crash the app.
        }
    }

    // File.AppendAllText opens with FileShare.Read, which blocks a second WRITER, and the
    // resulting IOException was swallowed by the caller's catch with no retry: the line was
    // simply lost. Not hypothetical for this app. The configurator's "Test Argument" runs
    // MatrixDesktopConfigurator.exe and MatrixDesktop.exe at the same time and both write
    // here, so diagnostics went missing in exactly the situation that needed them. The
    // _gate lock is per process and buys nothing across processes.
    //
    // Measured with 8 processes writing 60 lines each:
    //     AppendAllText, no retry             18.3% of lines lost
    //     FileShare.ReadWrite, bounded retry  30.6% lost   <- worse, see below
    //     FileShare.Read, bounded retry        0.0% lost, 703 ms
    //
    // FileShare.ReadWrite is the trap. It lets two writers hold the file at once, and
    // .NET's FileMode.Append seeks to the end at OPEN time rather than using Windows
    // FILE_APPEND_DATA semantics, so both writers record the same offset and overwrite each
    // other. Keeping other writers out and retrying is what actually works. Readers are
    // still admitted, which is what the verification gate needs to tail this file.
    private const int AppendAttempts = 20;
    private const int AppendRetryDelayMs = 3;

    private static void AppendShared(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    _logPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: false);
                stream.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (Exception ex) when (
                (ex is IOException || ex is UnauthorizedAccessException) && attempt < AppendAttempts)
            {
                System.Threading.Thread.Sleep(AppendRetryDelayMs);
            }
        }
    }

    private static string ResolveLogPath()
    {
        // %LOCALAPPDATA%\MatrixDesktop\MatrixDesktop.log, with %TEMP% fallback for
        // restricted environments where LocalAppData isn't writable.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.GetTempPath();
        }
        return Path.Combine(appData, AppFolder, LogFileName);
    }

    private static void EnsureDirectoryExists()
    {
        if (_dirCreated) return;

        lock (_gate)
        {
            if (_dirCreated) return;
            try
            {
                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                _dirCreated = true;
            }
            catch
            {
                // Will be retried on next write.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_logPath)) return;
            var fi = new FileInfo(_logPath);
            if (fi.Length <= MaxBytes) return;
            var oldPath = _logPath + ".old";
            File.Move(_logPath, oldPath, overwrite: true);
        }
        catch
        {
            // Ignore — rotation is best-effort.
        }
    }
}
