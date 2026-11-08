using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MatrixDesktopConfigurator;

// One configurator per preset store. Every mutator saves the whole in-memory state over the
// store, so a second window on the same store silently erased presets saved by the first
// (and the single .bak rotated in the same close). Keyed on the resolved store path, so two
// portable installs with separate stores still run side by side.
//
// The decision is createdNew, never WaitOne: a named mutex re-enters on its owning thread,
// so a WaitOne check would admit a second acquisition on the same thread. Ownership is not
// taken at all; holding the handle keeps the named object alive, and the kernel destroys it
// when the last handle closes, including when the owning process dies.
internal sealed class SingleInstanceGuard : IDisposable
{
    private Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    // Also the window property the owning configurator puts on its main window, so a refused
    // launch can find the exact window that owns this store.
    public static string ObjectName(string storePath)
    {
        var normalized = Path.GetFullPath(storePath).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "BigFnJ.MatrixDesktop.Configurator." + Convert.ToHexString(hash, 0, 8);
    }

    public static string MutexName(string storePath) => @"Local\" + ObjectName(storePath);

    // Returns null when another configurator already holds this store. Access denied means
    // the name exists but belongs to a process this one cannot open (an elevated
    // configurator on the same store), which is the same answer.
    public static SingleInstanceGuard? TryAcquire(string storePath)
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName(storePath), out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (createdNew)
        {
            return new SingleInstanceGuard(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _mutex?.Dispose();
        _mutex = null;
    }
}
