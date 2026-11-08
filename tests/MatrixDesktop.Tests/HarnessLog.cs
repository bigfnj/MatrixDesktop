using System.Reflection;
using MatrixDesktop.Shared;

namespace MatrixDesktop.Tests;

// Production code these cases run logs through Shared/Logger.cs (AppCli and StorageService warn
// on the bad input the cases feed them), and Logger defaults to the owner's real
// %LOCALAPPDATA%\MatrixDesktop\MatrixDesktop.log, so every test run used to append to it
// (AUD-80). Program.cs points Logger at a per-run fixture before anything else runs, and Verify
// runs after every selected case to prove nothing moved it back.
internal static class HarnessLog
{
    internal const string CaseName = "Diagnostics logging stays in this run's fixture log";
    private const string Prefix = "matrixdesktop-tests-log-";
    private static readonly FieldInfo PathField = typeof(Logger).GetField("_logPath", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Logger no longer has a _logPath field, so the harness cannot see where it writes");

    internal static readonly string Folder = Path.Combine(Path.GetFullPath(Path.GetTempPath()), Prefix + Guid.NewGuid().ToString("N"));
    internal static readonly string LogPath = Path.Combine(Folder, "MatrixDesktop.log");

    internal static string CurrentPath => (string)PathField.GetValue(null)!;

    internal static void Redirect() => Logger.ResetForTests(LogPath);

    internal static void Verify()
    {
        var current = CurrentPath;
        var owner = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MatrixDesktop") + Path.DirectorySeparatorChar;
        // Paths first, so a broken redirect is reported without writing a line to the owner's log.
        Check.False(current.StartsWith(owner, StringComparison.OrdinalIgnoreCase), $"the logger writes to the owner's real log at '{current}'");
        Check.Equal(LogPath, current, "the logger must write to this run's fixture log");
        var marker = "harness log witness " + Guid.NewGuid().ToString("N");
        Logger.Info(marker);
        Check.True(File.Exists(LogPath) && File.ReadAllText(LogPath).Contains(marker, StringComparison.Ordinal),
            "a logged line must land in this run's fixture log");
    }

    // A passing run leaves nothing behind; a failing one keeps its log and says where.
    internal static void Finish(bool passed)
    {
        if (!Directory.Exists(Folder)) return;
        if (!passed)
        {
            Console.WriteLine($"Diagnostics log kept at {LogPath}");
            return;
        }
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(Folder) == parent && Path.GetFileName(Folder).StartsWith(Prefix, StringComparison.Ordinal))
        {
            Directory.Delete(Folder, recursive: true);
        }
    }
}
