using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace MatrixDesktop;

// One live wallpaper per desktop session and isolation scope, and the newest launch wins. A
// second --wallpaper used to stack another renderer and tray icon on the same WorkerW, attached
// under the first, so its new settings never showed. The newcomer posts a request on the scope's
// request board, asks the holder to exit through a named event and waits a bounded time for the
// named mutex. The holder claims the request on its own thread before it exits (R2-04). A
// newcomer withdraws an unclaimed request at its deadline, so a holder that does not hand over
// in time keeps the desktop and the newcomer stops; a newcomer that a newer launch superseded
// stops quietly, so of two overlapping launches the newer one wins.
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

    internal enum Outcome { Acquired, TookOver, TimedOut, Unavailable, Superseded }

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _exitRequest;
    private readonly RequestBoard? _board;
    private RegisteredWaitHandle? _listener;
    private bool _owned;

    internal Outcome Result { get; }
    internal string? Problem { get; }

    private WallpaperInstanceGuard(Mutex? mutex, EventWaitHandle? exitRequest, RequestBoard? board, bool owned, Outcome result, string? problem = null)
    {
        _mutex = mutex; _exitRequest = exitRequest; _board = board; _owned = owned; Result = result; Problem = problem;
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
        RequestBoard board;
        Mutex mutex;
        bool createdNew;
        try
        {
            exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Exit");
            try { board = RequestBoard.Open(name); }
            catch { exitRequest.Dispose(); throw; }
            try { mutex = new Mutex(initiallyOwned: true, name, out createdNew); }
            catch { board.Dispose(); exitRequest.Dispose(); throw; }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // Objects this launch may not open (an instance started elevated creates them with a
            // High label) must not fail the launch: it runs unguarded and the caller says so.
            return new WallpaperInstanceGuard(null, null, null, false, Outcome.Unavailable, error.Message);
        }
        var ticket = board.NextTicket();
        var owned = createdNew;
        var superseded = false;
        if (!owned)
        {
            superseded = !board.Request(ticket);
            if (!superseded) owned = WaitForHolder(mutex, exitRequest, board, ticket, bound, ref superseded);
            if (!owned && !superseded)
            {
                // At the deadline: withdraw the request, unless the holder already claimed it, or
                // an older request this one replaced, and is on its way out, which gets one more
                // bound.
                var withdrawal = board.Withdraw(ticket);
                superseded = withdrawal == Withdrawal.Superseded;
                if (withdrawal == Withdrawal.Claimed && !(owned = WaitForHolder(mutex, exitRequest, board, ticket, bound, ref superseded))) board.Clear(ticket);
            }
            if (owned) board.Clear(ticket);
            if (owned && board.NewerThan(ticket))
            {
                // A newer launch asked meanwhile: hand the scope straight to it, never show.
                try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned by this thread */ }
                owned = false;
                superseded = true;
            }
        }
        // A request aimed at the previous holder must not end this one.
        if (owned) exitRequest.Reset();
        return new WallpaperInstanceGuard(mutex, exitRequest, board, owned,
            createdNew ? Outcome.Acquired : owned ? Outcome.TookOver : superseded ? Outcome.Superseded : Outcome.TimedOut);
    }

    // Repeat the request until the holder releases, a newer launch supersedes this one or the
    // bound passes, so a holder that had not started listening yet, or a request another
    // newcomer's reset swallowed, is still asked.
    private static bool WaitForHolder(Mutex mutex, EventWaitHandle exitRequest, RequestBoard board, long ticket, TimeSpan bound, ref bool superseded)
    {
        var deadline = Environment.TickCount64 + (long)bound.TotalMilliseconds;
        while (true)
        {
            if (board.NewerThan(ticket)) { superseded = true; return false; }
            exitRequest.Set();
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return false;
            try { if (mutex.WaitOne((int)Math.Min(250, remaining))) return true; }
            catch (AbandonedMutexException) { return true; } // the holder ended without releasing
        }
    }

    // Runs onTakeover once, on a thread-pool thread, when a newer launch asks this holder to exit.
    // Call again after a ClaimTakeover that returned false, to keep listening.
    internal void ListenForTakeover(Action onTakeover)
    {
        if (!_owned || _exitRequest is null) return;
        _listener?.Unregister(null);
        _listener = ThreadPool.RegisterWaitForSingleObject(_exitRequest,(_, _) => onTakeover(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    // The holder calls this on its own thread after ListenForTakeover fired, before it exits. True
    // claims the standing request; false means there is none (the newcomer withdrew it at its
    // deadline, or the signal was stale), so the holder keeps the desktop and listens again.
    internal bool ClaimTakeover() => _owned && _board is not null && _board.Claim();

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
        _board?.Dispose();
    }

    private enum Withdrawal { Withdrawn, Claimed, Superseded }

    // The scope's request board: the last ticket issued, the newest ticket that asked for the
    // desktop, and the standing request (a ticket, with Claimed set once the holder took it or an
    // older request this one replaced).
    // Every launch in the scope keeps it open; a named mutex serialises the three values.
    private sealed class RequestBoard : IDisposable
    {
        private const long Claimed = 1L << 62;
        private const int SequenceAt = 0, NewestAt = 8, RequestAt = 16;
        private readonly MemoryMappedFile _section;
        private readonly MemoryMappedViewAccessor _view;
        private readonly Mutex _lock;

        private RequestBoard(MemoryMappedFile section, MemoryMappedViewAccessor view, Mutex gate)
        {
            _section = section; _view = view; _lock = gate;
        }

        internal static RequestBoard Open(string name)
        {
            // Named sections are Windows-only; elsewhere the caller runs unguarded and says why.
            if (!OperatingSystem.IsWindows()) throw new IOException("The wallpaper request board needs Windows named sections.");
            var section = MemoryMappedFile.CreateOrOpen(name + ".Board", 24);
            MemoryMappedViewAccessor? view = null;
            try
            {
                view = section.CreateViewAccessor(0, 24);
                return new RequestBoard(section, view, new Mutex(false, name + ".BoardLock"));
            }
            catch { view?.Dispose(); section.Dispose(); throw; }
        }

        private T Locked<T>(Func<T> body)
        {
            try { _lock.WaitOne(); }
            catch (AbandonedMutexException) { /* a launch ended inside; each value is still whole */ }
            try { return body(); }
            finally { _lock.ReleaseMutex(); }
        }

        internal long NextTicket() => Locked(() => { var next = _view.ReadInt64(SequenceAt) + 1; _view.Write(SequenceAt, next); return next; });

        internal bool NewerThan(long ticket) => Locked(() => _view.ReadInt64(NewestAt) > ticket);

        // False when a newer launch already asked: this one is superseded and posts nothing. A
        // holder that claimed an older request is leaving whichever newcomer it answered, so the
        // newer request keeps the Claimed bit (R3-09).
        internal bool Request(long ticket) => Locked(() =>
        {
            if (_view.ReadInt64(NewestAt) > ticket) return false;
            _view.Write(NewestAt, ticket);
            _view.Write(RequestAt, (_view.ReadInt64(RequestAt) & Claimed) != 0 ? ticket | Claimed : ticket);
            return true;
        });

        internal bool Claim() => Locked(() =>
        {
            var request = _view.ReadInt64(RequestAt);
            if (request == 0) return false;
            _view.Write(RequestAt, request | Claimed);
            return true;
        });

        internal Withdrawal Withdraw(long ticket) => Locked(() =>
        {
            var request = _view.ReadInt64(RequestAt);
            if (request == ticket) { _view.Write(RequestAt, 0L); return Withdrawal.Withdrawn; }
            return request == (ticket | Claimed) ? Withdrawal.Claimed : Withdrawal.Superseded;
        });

        // Clears this ticket's own request, claimed or not, and never a newer one.
        internal void Clear(long ticket) => Locked(() =>
        {
            if ((_view.ReadInt64(RequestAt) & ~Claimed) == ticket) _view.Write(RequestAt, 0L);
            return 0;
        });

        public void Dispose()
        {
            _view.Dispose();
            _section.Dispose();
            _lock.Dispose();
        }
    }
}
