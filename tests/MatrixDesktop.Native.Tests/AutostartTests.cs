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
        ("Native startup shortcut preserves target and exact wallpaper arguments", () => OnStaThread(ShortcutRoundtrip)),
        ("Native startup shortcut state tells this launch's settings from another launch's", () => OnStaThread(ShortcutState)),
        ("Native startup tray click stores this launch's settings over another launch's", ClickDecision),
        ("Native startup shortcut's own launch reads as this launch and stores the same text", () => OnStaThread(ShortcutOwnLaunch)),
    ];

    // R3-03. The stored arguments always gained one more --wallpaper, so the wallpaper the shortcut
    // started at sign-in compared "A --wallpaper" with "A --wallpaper --wallpaper", read as other
    // settings, and a click meant to remove the shortcut re-saved it one token longer instead.
    private static void ShortcutOwnLaunch()
    {
        var root = Path.Combine(Path.GetTempPath(), "matrixdesktop-autostart-own-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "own launch 漢字.exe");
        var shortcut = Path.Combine(root, "own launch 漢字.lnk");
        Directory.CreateDirectory(root);
        File.Copy(Environment.ProcessPath!, executable);
        try
        {
            string[] launch = ["--wallpaper", "--scene", "zmatrix", "--zmPhrases", JsonSerializer.Serialize(new[] { "a \"quoted\" phrase" })];
            AutostartService.SetEnabledAt(true, launch, shortcut, executable, root);
            var stored = ReadShortcut(shortcut).Arguments;
            var started = ParseArguments(stored);
            Require(AutostartService.StateAt(shortcut, executable, started) == AutostartState.ThisLaunch,
                "The launch the shortcut starts must read as this launch, not as other settings; stored: " + stored);
            AutostartService.SetEnabledAt(true, started, shortcut, executable, root);
            Require(ReadShortcut(shortcut).Arguments == stored, "Storing from the shortcut's own launch must save the same arguments, not one more --wallpaper.");
            string[] typedLast = ["--scene", "zmatrix", "--wallpaper"];
            AutostartService.SetEnabledAt(true, typedLast, shortcut, executable, root);
            Require(ParseArguments(ReadShortcut(shortcut).Arguments).SequenceEqual(typedLast), "A typed trailing --wallpaper must be stored once.");
            Require(AutostartService.StateAt(shortcut, executable, typedLast) == AutostartState.ThisLaunch, "The launch that typed --wallpaper last must read as this launch.");
        }
        finally
        {
            File.Delete(shortcut);
            File.Delete(executable);
            Directory.Delete(root);
        }
    }

    // R2-03. The tray ticked Start with Windows whenever a shortcut existed, so a shortcut another
    // launch stored read as this launch's and the first click deleted it.
    private static void ShortcutState()
    {
        var root = Path.Combine(Path.GetTempPath(), "matrixdesktop-autostart-state-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "state target 漢字.exe");
        var shortcut = Path.Combine(root, "owned wallpaper 漢字.lnk");
        var unreadable = Path.Combine(root, "unreadable.lnk");
        Directory.CreateDirectory(root);
        File.Copy(Environment.ProcessPath!, executable);
        File.WriteAllBytes(unreadable, [77, 68, 45, 116, 101, 115, 116]);
        try
        {
            string[] thisLaunch = ["--scene", "zmatrix", "--zmPhrases", JsonSerializer.Serialize(new[] { "a \"quoted\" phrase" }), "--windowed", ""];
            string[] otherLaunch = ["--scene", "zmatrix", "--zmPhrases", JsonSerializer.Serialize(new[] { "a \"quoted\" phrasf" }), "--windowed", ""];
            Require(AutostartService.StateAt(shortcut, executable, thisLaunch) == AutostartState.Off, "No shortcut must read as off.");
            AutostartService.SetEnabledAt(true, thisLaunch, shortcut, executable, root);
            Require(AutostartService.StateAt(shortcut, executable, thisLaunch) == AutostartState.ThisLaunch, "A shortcut holding exactly this launch's arguments must read as this launch.");
            Require(AutostartService.StateAt(shortcut, executable, otherLaunch) == AutostartState.OtherSettings,
                "A shortcut another launch stored, differing only inside a quoted JSON value, must read as other settings, not as this launch.");
            Require(AutostartService.StateAt(shortcut, Path.Combine(root, "another target.exe"), thisLaunch) == AutostartState.OtherSettings, "A shortcut that starts another executable must read as other settings.");
            Require(AutostartService.StateAt(unreadable, executable, thisLaunch) == AutostartState.OtherSettings, "A shortcut that cannot be read must read as other settings.");
            AutostartService.SetEnabledAt(false, [], shortcut, executable, root);
            Require(AutostartService.StateAt(shortcut, executable, thisLaunch) == AutostartState.Off, "A deleted shortcut must read as off.");
        }
        finally
        {
            File.Delete(shortcut);
            File.Delete(unreadable);
            File.Delete(executable);
            Directory.Delete(root);
        }
    }

    private static void ClickDecision()
    {
        Require(AutostartService.ClickEnables(AutostartState.OtherSettings), "A click on a shortcut holding another launch's settings must store this launch's, never delete it.");
        Require(AutostartService.ClickEnables(AutostartState.Off), "A click with no shortcut must create one.");
        Require(!AutostartService.ClickEnables(AutostartState.ThisLaunch), "A click on this launch's own shortcut must remove it.");
    }

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
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
