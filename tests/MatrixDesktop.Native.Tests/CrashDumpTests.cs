using System.Reflection;
using MatrixDesktop.Shared;

internal static class CrashDumpTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Native crash diagnostics never release an unreserved slot", BeforeReservation),
        ("Native crash diagnostics release only failed reserved writes", FailedWrites),
        ("Native diagnostics log rotates on the first write of a short-lived process", FirstWriteRotation),
        ("Native diagnostics log checks rotation again 10 writes after a check", PeriodicRotation),
    ];

    private static readonly FieldInfo Counter = typeof(CrashDumpWriter).GetField("_dumpsWritten", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static int Count => (int)Counter.GetValue(null)!;
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void WithFixture(Action<string> test)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "matrix-dump-reservation-" + Guid.NewGuid().ToString("N")));
        var previous = Count;
        Counter.SetValue(null, 0);
        Directory.CreateDirectory(root);
        // The writer logs every outcome; keep those lines out of the owner's real log.
        Logger.ResetForTests(Path.Combine(root, "MatrixDesktop.log"));
        try { test(root); }
        finally
        {
            Logger.ResetForTests(null);
            Counter.SetValue(null, previous);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Require(Path.GetDirectoryName(root) == parent && Path.GetFileName(root).StartsWith("matrix-dump-reservation-", StringComparison.Ordinal), "Cleanup must remain in the owned synthetic dump fixture.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyCap(string root)
    {
        var writes = 0;
        var results = Enumerable.Range(0, 5).Select(_ => CrashDumpWriter.WriteDumpWithBackend("synthetic cap",
            () => Path.Combine(root, "valid"), path =>
            {
                Require(Path.GetDirectoryName(path) == Path.Combine(root, "valid"), "The injected writer must receive only the owned synthetic directory.");
                writes++;
                return true; // Count an observed backend success; never call MiniDumpWriteDump.
            })).ToArray();
        Require(writes == 3 && results.Count(path => path is not null) == 3, "Exactly three successful backend writes may be reserved after failures recover.");
        Require(Count == 3, "Rejected over-cap attempts must not retain a provisional reservation.");
    }

    private static void BeforeReservation() => WithFixture(root =>
    {
        var blockingFile = Path.Combine(root, "file-not-directory");
        File.WriteAllText(blockingFile, "synthetic fixture");
        var writes = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = CrashDumpWriter.WriteDumpWithBackend("synthetic directory failure",
                () => Path.Combine(blockingFile, "dumps"), _ => { writes++; return true; });
            Require(result is null && writes == 0, "A real directory creation failure must precede the injected writer.");
            Require(Count == 0, "A pre-reservation folder failure must not decrement the reservation counter.");
        }
        VerifyCap(root);
    });

    private static void FailedWrites() => WithFixture(root =>
    {
        var writes = 0;
        foreach (var throws in new[] { false, true })
        {
            var result = CrashDumpWriter.WriteDumpWithBackend("synthetic writer failure", () => Path.Combine(root, "valid"), _ =>
            {
                writes++;
                if (throws) throw new IOException("Synthetic backend failure.");
                return false;
            });
            Require(result is null && Count == 0, "A failed reserved write must return its slot exactly once.");
        }
        Require(writes == 2, "Both the false-result and throwing backend must actually execute.");
        VerifyCap(root);
    });

    // A screensaver run writes two or three lines and exits, so the log must check its size on
    // a process's first write, not only after several.
    private static void FirstWriteRotation() => WithFixture(root =>
    {
        var log = Path.Combine(root, "MatrixDesktop.log");
        File.WriteAllBytes(log, new byte[2_100_000]);
        Logger.Info("synthetic short-lived process line");
        var old = new FileInfo(log + ".old");
        Require(old.Exists, "An oversized log must rotate on a process's first write: no .old file was created.");
        Require(old.Length == 2_100_000, $"The rotated file must hold the whole previous log, got {old.Length} bytes.");
        var live = new FileInfo(log);
        Require(live.Exists && live.Length < 200, $"The live log must restart with only the new line, got {(live.Exists ? live.Length : -1)} bytes.");
        Require(File.ReadAllText(log).Contains("synthetic short-lived process line", StringComparison.Ordinal), "The line must land in the fixture log.");
    });

    // Anchored to an observed check rather than to process start, so this case does not depend
    // on which write performs the first check.
    private static void PeriodicRotation() => WithFixture(root =>
    {
        var log = Path.Combine(root, "MatrixDesktop.log");
        var old = log + ".old";
        File.WriteAllBytes(log, new byte[2_100_000]);
        for (var line = 1; line <= 10 && !File.Exists(old); line++) Logger.Info($"synthetic line {line} before the first check");
        Require(File.Exists(old), "An oversized log must rotate within a process's first 10 writes.");
        File.Delete(old);
        using (var grow = new FileStream(log, FileMode.Append, FileAccess.Write)) grow.Write(new byte[2_100_000]);
        for (var line = 1; line <= 10; line++)
        {
            Logger.Info($"synthetic line {line} after a check");
            Require(line == 10 || !File.Exists(old), $"The log rotated early, on write {line} after the previous check.");
        }
        Require(File.Exists(old), "The log must check its size again on the 10th write after the previous check: no .old file was created.");
    });
}
