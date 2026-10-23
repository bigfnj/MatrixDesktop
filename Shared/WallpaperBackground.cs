using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MatrixDesktop.Shared;

// Captures only the shell's wallpaper/pattern, never another application's pixels.
internal static class WallpaperBackground
{
    internal sealed record Capture(byte[] Png, int Width, int Height, int X, int Y, double[] BackgroundColor);

    internal static Capture Create(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 67_108_864)
            throw new InvalidOperationException("The wallpaper dimensions exceed the supported capture size.");
        var background = GetSysColor(1);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb((int)(background & 255), (int)((background >> 8) & 255), (int)((background >> 16) & 255)));
            var dc = graphics.GetHdc();
            try
            {
                SetWindowOrgEx(dc, bounds.X, bounds.Y, 0);
                if (!PaintDesktop(dc)) throw new InvalidOperationException("Windows could not paint the desktop wallpaper.");
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new Capture(stream.ToArray(), bounds.Width, bounds.Height, bounds.X, bounds.Y,
            [(background & 255) / 255.0, ((background >> 8) & 255) / 255.0, ((background >> 16) & 255) / 255.0]);
    }

    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")] private static extern bool PaintDesktop(nint dc);
    [DllImport("gdi32.dll")] private static extern bool SetWindowOrgEx(nint dc, int x, int y, nint previous);
}
