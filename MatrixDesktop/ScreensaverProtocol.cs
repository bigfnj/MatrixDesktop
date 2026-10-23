using System.Diagnostics;
using System.Globalization;
using Shared;

namespace MatrixDesktop;

internal static class ScreensaverProtocol
{
    public static bool TryHandle(string[] args, out HostLaunchPlan? plan)
    {
        plan = null;
        if (args.Length == 0)
        {
            if (!string.Equals(Path.GetExtension(Environment.ProcessPath), ".scr", StringComparison.OrdinalIgnoreCase)) return false;
            args = ["/c"];
        }
        var token = args[0].Trim();
        var separator = token.IndexOf(':');
        var command = (separator < 0 ? token : token[..separator]).ToLowerInvariant();
        if (command is not ("/s" or "-s" or "/c" or "-c" or "/p" or "-p" or "--register-screensaver" or "--restore-screensaver")) return false;
        if (command is "/c" or "-c")
        {
            var configurator = Path.Combine(AppContext.BaseDirectory, "MatrixDesktopConfigurator.exe");
            if (!File.Exists(configurator)) throw new FileNotFoundException("The screensaver settings require MatrixDesktopConfigurator.exe beside MatrixDesktop.scr.", configurator);
            using var configurationProcess = Process.Start(new ProcessStartInfo(configurator)
                { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
            return true;
        }
        if (command == "--register-screensaver")
        {
            ScreensaverRegistration.Register(Path.Combine(AppContext.BaseDirectory, "MatrixDesktop.scr"));
            return true;
        }
        if (command == "--restore-screensaver")
        {
            ScreensaverRegistration.Restore();
            return true;
        }
        var profile = ScreensaverProfile.Load();
        if (command is "/s" or "-s")
        {
            plan = new HostLaunchPlan(profile, HostLaunchKind.Screensaver);
            return true;
        }
        var parentText = separator >= 0 ? token[(separator + 1)..] : args.Length > 1 ? args[1] : "";
        if (!long.TryParse(parentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var handle) || handle == 0 || (IntPtr.Size == 4 && (handle < int.MinValue || handle > uint.MaxValue)))
            throw new ArgumentException("Screensaver preview requires /p followed by a valid parent window handle.");
        plan = new HostLaunchPlan(profile, HostLaunchKind.ScreensaverPreview, (nint)handle);
        return true;
    }
}
