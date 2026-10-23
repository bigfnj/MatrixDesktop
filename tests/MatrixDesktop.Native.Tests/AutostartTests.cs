using MatrixDesktop;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;

internal static class AutostartTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Native startup shortcut preserves target and exact wallpaper arguments", OnStaThread),
    ];

    private static void OnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { ShortcutRoundtrip(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new InvalidOperationException("The owned native shortcut operation did not finish within 20 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void ShortcutRoundtrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "matrixdesktop-autostart-" + Guid.NewGuid().ToString("N"));
        var workingDirectory = Path.Combine(root, "working folder 漢字");
        var executable = Path.Combine(root, "native target 漢字.exe");
        var shortcut = Path.Combine(root, "owned wallpaper 漢字.lnk");
        var neighbor = Path.Combine(root, "untouched-neighbor.lnk");
        Directory.CreateDirectory(workingDirectory);
        File.Copy(Environment.ProcessPath!, executable);
        var neighborBytes = new byte[] { 77, 68, 45, 116, 101, 115, 116 };
        File.WriteAllBytes(neighbor, neighborBytes);
        try
        {
            string[] arguments = ["--scene", "zmatrix", "--zmFont", JsonSerializer.Serialize(new { name = "矩阵 Test Font", height = -17 }),
                "--zmPhrases", JsonSerializer.Serialize(new[] { "こんにちは Matrix", "a \"quoted\" phrase", @"C:\with spaces\tail\" }),
                "--windowed", "", @"C:\ends with space \", "%USERPROFILE%& literal! (parentheses)"];
            AutostartService.SetEnabledAt(true, arguments, shortcut, executable, workingDirectory);
            Require(File.Exists(shortcut), "The real Windows shortcut must be saved at the owned fixture path.");
            var (target, directory, command) = ReadShortcut(shortcut);
            Require(Path.GetFullPath(target).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase), "The shortcut must preserve its exact Unicode target path.");
            Require(Path.GetFullPath(directory).Equals(Path.GetFullPath(workingDirectory), StringComparison.OrdinalIgnoreCase), "The shortcut must preserve its exact Unicode working directory.");
            var parsed = ParseArguments(command);
            Require(parsed.SequenceEqual(arguments.Concat(["--wallpaper"])), "Shortcut arguments must roundtrip Unicode, JSON, quotes, empty values and trailing slashes, then force wallpaper last.");
            AutostartService.SetEnabledAt(false, [], shortcut, executable, workingDirectory);
            Require(!File.Exists(shortcut), "Disabling startup must delete its own shortcut.");
            Require(File.Exists(executable) && File.ReadAllBytes(neighbor).SequenceEqual(neighborBytes), "Disabling startup must preserve the target and neighboring files.");
            AutostartService.SetEnabledAt(false, [], shortcut, executable, workingDirectory);
        }
        finally
        {
            File.Delete(shortcut);
            File.Delete(neighbor);
            File.Delete(executable);
            Directory.Delete(workingDirectory);
            Directory.Delete(root);
        }
    }

    private static (string Target, string Directory, string Arguments) ReadShortcut(string path)
    {
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")) ?? throw new InvalidOperationException("Windows shortcut COM support must be present.");
        object? shortcut = null;
        try
        {
            shortcut = Activator.CreateInstance(type)!;
            ((IPersistFile)shortcut).Load(path, 0);
            var link = (IReadShellLinkW)shortcut;
            var target = new StringBuilder(32768); var directory = new StringBuilder(32768); var arguments = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, 0, 4);
            link.GetWorkingDirectory(directory, directory.Capacity);
            link.GetArguments(arguments, arguments.Capacity);
            return (target.ToString(), directory.ToString(), arguments.ToString());
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
        }
    }

    private static string[] ParseArguments(string arguments)
    {
        var pointer = CommandLineToArgv("native-probe.exe " + arguments, out var count);
        if (pointer == 0) throw new InvalidOperationException("Windows could not parse the saved shortcut arguments.");
        try { return Enumerable.Range(1, count - 1).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "CommandLineToArgvW")] private static extern nint CommandLineToArgv(string command, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IReadShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maximum, nint findData, uint flags);
        void GetIDList(out nint item);
        void SetIDList(nint item);
        void GetDescription(nint description, int maximum);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maximum);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximum);
    }
}
