using System.Reflection;
using MatrixDesktop.Shared;

// CrashDumpWriter logs every dump outcome through Shared/Logger.cs, and Logger defaults to the
// owner's real %LOCALAPPDATA%\MatrixDesktop\MatrixDesktop.log. A case that restored that default
// after using its own fixture put every later case back on the owner's log (AUD-80). Program.cs
// points Logger at a per-run fixture before anything else runs, and Verify runs after every
// selected case to prove nothing moved it back.
internal static class HarnessLog
{
    internal const string CaseName = "Native diagnostics logging stays in this run's fixture log";
    private const string Prefix = "matrixdesktop-native-tests-log-";
    private static readonly FieldInfo PathField = typeof(Logger).GetField("_logPath", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Logger no longer has a _logPath field, so the harness cannot see where it writes");

    internal static readonly string Folder = Path.Combine(Path.GetFullPath(Path.GetTempPath()), Prefix + Guid.NewGuid().ToString("N"));
    internal static readonly string LogPath = Path.Combine(Folder, "MatrixDesktop.log");

    internal static string CurrentPath => (string)PathField.GetValue(null)!;

    internal static void Redirect() => Logger.ResetForTests(LogPath);

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static void Verify()
    {
        var current = CurrentPath;
        var owner = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MatrixDesktop") + Path.DirectorySeparatorChar;
        // Paths first, so a broken redirect is reported without writing a line to the owner's log.
        Require(!current.StartsWith(owner, StringComparison.OrdinalIgnoreCase), $"The logger writes to the owner's real log at '{current}'.");
        Require(current == LogPath, $"The logger must write to this run's fixture log '{LogPath}', not '{current}'.");
        var marker = "native harness log witness " + Guid.NewGuid().ToString("N");
        Logger.Info(marker);
        Require(File.Exists(LogPath) && File.ReadAllText(LogPath).Contains(marker, StringComparison.Ordinal),
            "A logged line must land in this run's fixture log.");
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
