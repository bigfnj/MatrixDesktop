using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MatrixDesktop.Shared;

// Window icon loading, shared by both executables. Previously this file existed twice,
// byte-identical apart from its namespace line.
//
// The icon is read from the running executable's OWN Win32 resources, not from a managed
// embedded resource. That is a deliberate change from the original design, and the reason
// is that the icon was in the payload six times:
//
//     twice per .dll   -- a managed EmbeddedResource, plus the Win32 icon resource the SDK
//                         stamps into the assembly from <ApplicationIcon>
//     once per .exe    -- the apphost, which CreateAppHost populates by COPYING the Win32
//                         resources out of the .dll it was built for
//
// The apphost sourcing its icon from the assembly is why <ApplicationIcon> cannot simply be
// dropped: doing that leaves the .exe with the blank default apphost icon. But the managed
// copy is pure duplication of bytes that are already in the .exe, so it is the one that can
// go, with no PE surgery and no build-time machinery. Removing it took 114,970 bytes out of
// the publish payload.
//
// Verified rather than assumed, because a wrong icon is silent: PrivateExtractIcons against
// the .exe returns pixel-identical bitmaps to the old managed lookup at 16, 32 and 48, SHA
// matched. Icon.ExtractAssociatedIcon is NOT an adequate substitute and is no longer used:
// it returns a single 32x32, and `new Icon(that, 16, 16)` hands back the 32x32 unchanged
// rather than the icon's real 16x16 entry. Measured: identical hash to the 32x32.
internal sealed class AppWindowIcon : IDisposable
{
    private const string IconFileName = "Matrix.ico";
    private const int WmSetIcon = 0x0080;
    private static readonly IntPtr IconSmall = new(0);
    private static readonly IntPtr IconBig = new(1);
    private static readonly IntPtr IconSmall2 = new(2);

    private readonly Icon? _small;
    private readonly Icon? _large;

    private AppWindowIcon(Icon? small, Icon? large)
    {
        _small = small;
        _large = large;
    }

    public static AppWindowIcon Load()
    {
        var small = LoadIcon(16, 16);
        var large = LoadIcon(32, 32);

        if (small is null && large is not null)
        {
            small = (Icon)large.Clone();
        }
        else if (large is null && small is not null)
        {
            large = (Icon)small.Clone();
        }

        return new AppWindowIcon(small, large);
    }

    public void ApplyTo(Form form)
    {
        form.ShowIcon = true;

        if (_large is not null)
        {
            form.Icon = _large;
        }

        if (!form.IsHandleCreated)
        {
            return;
        }

        try
        {
            if (_small is not null)
            {
                SendMessage(form.Handle, WmSetIcon, IconSmall, _small.Handle);
                SendMessage(form.Handle, WmSetIcon, IconSmall2, _small.Handle);
            }

            if (_large is not null)
            {
                SendMessage(form.Handle, WmSetIcon, IconBig, _large.Handle);
            }
        }
        catch
        {
            // Cosmetic only; keep app startup resilient.
        }
    }

    public void Dispose()
    {
        if (_small is not null && !ReferenceEquals(_small, _large))
        {
            _small.Dispose();
        }

        _large?.Dispose();
    }

    private static Icon? LoadIcon(int width, int height)
    {
        return LoadExecutableIcon(width, height)
            ?? LoadFileIcon(width, height);
    }

    private static Icon? LoadFileIcon(int width, int height)
    {
        try
        {
            // Retained as a fallback for an unusual host that gives PrivateExtractIcons
            // nothing, and for a dev-time run from a directory that happens to carry the
            // .ico. AppContext.BaseDirectory is process scoped, so it behaves the same
            // regardless of which assembly this file is compiled into. The publish payload
            // deliberately ships no loose Matrix.ico, so this normally finds nothing.
            var iconPath = Path.Combine(AppContext.BaseDirectory, IconFileName);
            return File.Exists(iconPath) ? new Icon(iconPath, width, height) : null;
        }
        catch
        {
            return null;
        }
    }

    private static Icon? LoadExecutableIcon(int width, int height)
    {
        var handles = new IntPtr[1];
        var ids = new int[1];

        try
        {
            // Asks the file for the requested SIZE, which is the whole point: the icon
            // carries real 16, 24, 32, 48, 64, 128 and 256 entries and the title bar and
            // alt-tab pull different ones. Icon.ExtractAssociatedIcon cannot express that.
            var found = PrivateExtractIcons(Application.ExecutablePath, 0, width, height, handles, ids, 1, 0);
            if (found <= 0 || handles[0] == IntPtr.Zero)
            {
                return null;
            }

            // Clone off the unmanaged handle so the Icon owns managed memory, then release
            // the HICON. Icon.FromHandle does NOT take ownership, so skipping DestroyIcon
            // here would leak a GDI handle per window per size.
            using var raw = Icon.FromHandle(handles[0]);
            return (Icon)raw.Clone();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handles[0] != IntPtr.Zero)
            {
                DestroyIcon(handles[0]);
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIcons(
        string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, int[] piconid, int nIcons, int flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
