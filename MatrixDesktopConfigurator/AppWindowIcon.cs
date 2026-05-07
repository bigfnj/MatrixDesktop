using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MatrixDesktopConfigurator;

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
        return LoadEmbeddedIcon(width, height)
            ?? LoadFileIcon(width, height)
            ?? LoadExecutableIcon();
    }

    private static Icon? LoadEmbeddedIcon(int width, int height)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(IconResourceName);
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
            var iconPath = Path.Combine(AppContext.BaseDirectory, IconResourceName);
            return File.Exists(iconPath) ? new Icon(iconPath, width, height) : null;
        }
        catch
        {
            return null;
        }
    }

    private static Icon? LoadExecutableIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
