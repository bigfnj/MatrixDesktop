using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace MatrixDesktop;

internal static class AutostartService
{
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MatrixDesktop Wallpaper.lnk");
    internal static bool Enabled => File.Exists(Shortcut);
    internal static void SetEnabled(bool enabled, IReadOnlyList<string> arguments)
        => SetEnabledAt(enabled, arguments, Shortcut, Path.Combine(AppContext.BaseDirectory, "MatrixDesktop.exe"), AppContext.BaseDirectory);

    internal static void SetEnabledAt(bool enabled, IReadOnlyList<string> arguments, string shortcutPath, string executable, string workingDirectory)
    {
        if (!enabled) { if (File.Exists(shortcutPath)) File.Delete(shortcutPath); return; }
        if (!File.Exists(executable)) throw new FileNotFoundException("MatrixDesktop.exe must remain beside the launch shortcut target.", executable);
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")) ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
        object? shortcut = null;
        try
        {
            shortcut = Activator.CreateInstance(type)!;
            var link = (IShellLinkW)shortcut;
            link.SetPath(executable);
            link.SetArguments(string.Join(" ", arguments.Concat(["--wallpaper"]).Select(QuoteArgument)));
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription("MatrixDesktop live wallpaper");
            ((IPersistFile)shortcut).Save(shortcutPath, true);
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
        }
    }

    internal static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') text.Append('\\', slashes * 2 + 1).Append('"');
            else text.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    // Use the Unicode shell API directly. The legacy scripting wrapper can map
    // Unicode paths through the process ANSI code page.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(nint path, int maximum, nint findData, uint flags);
        void GetIDList(out nint item);
        void SetIDList(nint item);
        void GetDescription(nint description, int maximum);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory(nint directory, int maximum);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(nint arguments, int maximum);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCommand(out int command);
        void SetShowCommand(int command);
        void GetIconLocation(nint iconPath, int maximum, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint owner, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
