using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace MatrixDesktop;

// R2-03. What the startup shortcut starts, compared with this launch: a shortcut another launch
// stored (the newest wallpaper launch wins, AUD-72) starts other settings at sign-in.
internal enum AutostartState { Off, ThisLaunch, OtherSettings }

internal static class AutostartService
{
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MatrixDesktop Wallpaper.lnk");
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "MatrixDesktop.exe");
    internal static AutostartState State(IReadOnlyList<string> arguments) => StateAt(Shortcut, Executable, arguments);
    internal static void SetEnabled(bool enabled, IReadOnlyList<string> arguments)
        => SetEnabledAt(enabled, arguments, Shortcut, Executable, AppContext.BaseDirectory);

    // One click stores this launch's settings unless the shortcut already starts exactly them.
    internal static bool ClickEnables(AutostartState state) => state != AutostartState.ThisLaunch;

    internal static string ShortcutArguments(IReadOnlyList<string> arguments) =>
        string.Join(" ", arguments.Concat(["--wallpaper"]).Select(QuoteArgument));

    // Exact match on target and arguments; a shortcut that cannot be read counts as other settings.
    internal static AutostartState StateAt(string shortcutPath, string executable, IReadOnlyList<string> arguments)
    {
        if (!File.Exists(shortcutPath)) return AutostartState.Off;
        object? shortcut = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")) ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
            shortcut = Activator.CreateInstance(type)!;
            ((IPersistFile)shortcut).Load(shortcutPath, 0);
            var link = (IShellLinkW)shortcut;
            var target = ReadString((buffer, capacity) => link.GetPath(buffer, capacity, 0, 0x4)); // SLGP_RAWPATH
            var stored = ReadString((buffer, capacity) => link.GetArguments(buffer, capacity));
            return string.Equals(Path.GetFullPath(target), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
                && stored == ShortcutArguments(arguments) ? AutostartState.ThisLaunch : AutostartState.OtherSettings;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or IOException or InvalidCastException)
        {
            return AutostartState.OtherSettings;
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
        }
    }

    private static string ReadString(Action<nint, int> read)
    {
        const int capacity = 32768;
        var buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            read(buffer, capacity);
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

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
            link.SetArguments(ShortcutArguments(arguments));
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
