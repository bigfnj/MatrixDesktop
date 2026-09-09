using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MatrixDesktop.Shared;

// Window icon loading, shared by both executables. Previously this file existed twice,
// byte-identical apart from its namespace line.
//
// The resource assembly is an explicit parameter rather than an ambient
// Assembly.GetExecutingAssembly() call, and that is load-bearing. Both csproj embed
// Matrix.ico under the logical name "Matrix.ico", so an ambient lookup only works while
// the loading code lives in the same assembly as the resource. Move this type into a
// class library and GetExecutingAssembly() would resolve to the library, which embeds
// nothing, and the icon would silently fall through to the on-disk copy. That fallback
// currently succeeds, so the regression would be invisible in every smoke test right up
// until someone pruned the duplicate icon from the publish output. Passing the assembly in
// makes the dependency explicit and impossible to break by relocating the file.
//
// Do not switch to Assembly.GetEntryAssembly(): it is null under some native hosts and
// returns the test host when running under a test runner.
internal sealed class AppWindowIcon : IDisposable
{
    private const string IconResourceName = "Matrix.ico";
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

    public static AppWindowIcon Load(Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        var small = LoadIcon(resourceAssembly, 16, 16);
        var large = LoadIcon(resourceAssembly, 32, 32);

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

    private static Icon? LoadIcon(Assembly resourceAssembly, int width, int height)
    {
        return LoadEmbeddedIcon(resourceAssembly, width, height)
            ?? LoadFileIcon(width, height)
            ?? LoadExecutableIcon(width, height);
    }

    private static Icon? LoadEmbeddedIcon(Assembly resourceAssembly, int width, int height)
    {
        try
        {
            using var stream = resourceAssembly.GetManifestResourceStream(IconResourceName);
            return stream is null ? null : new Icon(stream, width, height);
        }
        catch
        {
            return null;
        }
    }

    private static Icon? LoadFileIcon(int width, int height)
    {
        try
        {
            // AppContext.BaseDirectory is process scoped, so this fallback behaves the same
            // regardless of which assembly this code is compiled into.
            var iconPath = Path.Combine(AppContext.BaseDirectory, IconResourceName);
            return File.Exists(iconPath) ? new Icon(iconPath, width, height) : null;
        }
        catch
        {
            return null;
        }
    }

    private static Icon? LoadExecutableIcon(int width, int height)
    {
        try
        {
            using var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (extracted is null)
            {
                return null;
            }

            // ExtractAssociatedIcon ignores the size request and returns a single size, so
            // the requested variant is selected here. Without this the 16x16 title-bar slot
            // received a downscaled 32x32, which is a quality regression with no error.
            return new Icon(extracted, width, height);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
