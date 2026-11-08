using System.Security.Cryptography;
using System.Text;

namespace MatrixDesktop;

// One live wallpaper per desktop session and isolation scope, and the newest launch wins. A
// second --wallpaper used to stack another renderer and tray icon on the same WorkerW, attached
// under the first, so its new settings never showed. The newcomer asks the holder to exit
// through a named event and waits a bounded time for the named mutex; a holder that does not
// hand over in time keeps the desktop and the newcomer stops.
//
// The scope keeps a gate or test launch, which isolates its WebView2 profile through these
// variables, from ever ending the owner's running wallpaper, and the owner's from ending it.
internal sealed class WallpaperInstanceGuard : IDisposable
{
    internal static readonly string[] IsolationVariables =
    [
        "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
        "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE", "MATRIXDESKTOP_SCREENSAVER_PROFILE",
    ];

    internal enum Outcome { Acquired, TookOver, TimedOut, Unavailable }

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _exitRequest;
    private RegisteredWaitHandle? _listener;
    private bool _owned;

    internal Outcome Result { get; }
    internal string? Problem { get; }

    private WallpaperInstanceGuard(Mutex? mutex, EventWaitHandle? exitRequest, bool owned, Outcome result, string? problem = null)
    {
        _mutex = mutex; _exitRequest = exitRequest; _owned = owned; Result = result; Problem = problem;
    }

    // "default" for an ordinary launch; a stable hash of the isolation variables otherwise.
    internal static string Scope(Func<string, string?> environment)
    {
        var isolation = new StringBuilder();
        foreach (var name in IsolationVariables)
            if (environment(name) is { Length: > 0 } value) isolation.Append(name).Append('=').Append(value).Append('\n');
        return isolation.Length == 0 ? "default"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(isolation.ToString())))[..16];
    }

    // Call on the thread that will release it: a mutex belongs to the thread that acquired it.
    internal static WallpaperInstanceGuard Acquire(string scope, TimeSpan bound)
    {
        var name = $"Local\\MatrixDesktop.Wallpaper.{scope}";
        EventWaitHandle exitRequest;
        Mutex mutex;
        bool createdNew;
        try
        {
            exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Exit");
            try { mutex = new Mutex(initiallyOwned: true, name, out createdNew); }
            catch { exitRequest.Dispose(); throw; }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // Objects this launch may not open (an instance started elevated creates them with a
            // High label) must not fail the launch: it runs unguarded and the caller says so.
            return new WallpaperInstanceGuard(null, null, false, Outcome.Unavailable, error.Message);
        }
        var owned = createdNew;
        if (!owned)
        {
            // Repeat the request until the holder releases or the bound passes, so a holder
            // that had not started listening yet, or a request another newcomer's reset
            // swallowed, is still asked.
            var deadline = Environment.TickCount64 + (long)bound.TotalMilliseconds;
            while (!owned)
            {
                exitRequest.Set();
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) break;
                try { owned = mutex.WaitOne((int)Math.Min(250, remaining)); }
                catch (AbandonedMutexException) { owned = true; } // the holder ended without releasing
            }
        }
        // A request aimed at the previous holder must not end this one.
        if (owned) exitRequest.Reset();
        return new WallpaperInstanceGuard(mutex, exitRequest, owned, createdNew ? Outcome.Acquired : owned ? Outcome.TookOver : Outcome.TimedOut);
    }

    // Runs onTakeover once, on a thread-pool thread, when a newer launch asks this holder to exit.
    internal void ListenForTakeover(Action onTakeover)
    {
        if (!_owned || _listener is not null || _exitRequest is null) return;
        _listener = ThreadPool.RegisterWaitForSingleObject(_exitRequest,(_, _) => onTakeover(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    // Call on the acquiring thread, after the surface and tray are gone, so the newcomer
    // attaches to an empty desktop host.
    public void Dispose()
    {
        _listener?.Unregister(null);
        _listener = null;
        if (_owned)
        {
            _owned = false;
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { /* not owned by this thread */ }
        }
        _mutex?.Dispose();
        _exitRequest?.Dispose();
    }
}
