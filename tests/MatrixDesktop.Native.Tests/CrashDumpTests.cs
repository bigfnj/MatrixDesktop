using System.Reflection;
using MatrixDesktop.Shared;

internal static class CrashDumpTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Native crash diagnostics never release an unreserved slot", BeforeReservation),
        ("Native crash diagnostics release only failed reserved writes", FailedWrites),
        ("Native crash diagnostics never evict retained dumps on failed writes", FailedWritesKeepRetainedDumps),
        ("Native crash diagnostics let one thread at a time into the dump writer", SerializedWriters),
        ("Native crash diagnostics bound the wait for another thread's dump", BoundedWriterWait),
        ("Native crash diagnostics keep every same-instant dump under its own name", SameInstantNames),
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
        // The writer logs every outcome; keep those lines out of the owner's real log. Restore the
        // path the case found, normally the harness fixture: null would mean the owner's real log.
        var previousLog = HarnessLog.CurrentPath;
        Logger.ResetForTests(Path.Combine(root, "MatrixDesktop.log"));
        try { test(root); }
        finally
        {
            Logger.ResetForTests(previousLog);
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

    // Names sort oldest first, and each file is dated one minute after the previous one.
    private static string[] PlantDumps(string dir, DateTime oldestUtc)
    {
        Directory.CreateDirectory(dir);
        var names = Enumerable.Range(0, 10).Select(index => $"Synthetic-retained-{index:00}-pid1.dmp").ToArray();
        for (var index = 0; index < names.Length; index++)
        {
            var file = Path.Combine(dir, names[index]);
            File.WriteAllBytes(file, [7]);
            File.SetLastWriteTimeUtc(file, oldestUtc.AddMinutes(index));
        }
        return names;
    }

    private static string[] DumpNames(string dir) => Directory.GetFiles(dir, "*.dmp").Select(file => Path.GetFileName(file)).ToArray();

    // Like the native writer, each failing backend creates its file before the write fails.
    private static void FailedWritesKeepRetainedDumps() => WithFixture(root =>
    {
        var dir = Path.Combine(root, "dumps");
        var planted = PlantDumps(dir, DateTime.UtcNow.AddDays(-30));
        foreach (var throws in new[] { false, false, false, false, false, true })
        {
            var outcome = throws ? "a writer that threw" : "a writer that returned false";
            var created = 0;
            var result = CrashDumpWriter.WriteDumpWithBackend("synthetic failed write", () => dir, path =>
            {
                Require(Path.GetDirectoryName(path) == dir, "The injected writer must receive only the owned synthetic directory.");
                File.WriteAllBytes(path, [1, 2, 3]);
                created++;
                if (throws) throw new IOException("Synthetic failure after the dump file was created.");
                return false;
            });
            Require(result is null && created == 1 && Count == 0, $"The failed attempt with {outcome} must run once and return its slot.");
            var names = DumpNames(dir);
            var leftover = names.Except(planted).ToArray();
            Require(leftover.Length == 0, $"A failed attempt with {outcome} must delete the file it created, found {string.Join(", ", leftover)}.");
            var lost = planted.Except(names).ToArray();
            Require(lost.Length == 0, $"A failed attempt with {outcome} must not evict a retained dump, lost {string.Join(", ", lost)}.");
        }

        var written = CrashDumpWriter.WriteDumpWithBackend("synthetic success", () => dir, path => { File.WriteAllBytes(path, [1]); return true; });
        Require(written is not null && File.Exists(written), "A successful write must keep its new dump.");
        var after = DumpNames(dir);
        Require(after.Length == 10, $"A successful write must leave exactly 10 dumps, found {after.Length}.");
        Require(!after.Contains(planted[0]) && planted.Skip(1).All(after.Contains), "A successful write must evict only the oldest retained dump.");

        // Dumps written under a clock that was ahead must not make pruning delete the new dump.
        var future = Path.Combine(root, "future");
        PlantDumps(future, DateTime.UtcNow.AddDays(1));
        var newest = CrashDumpWriter.WriteDumpWithBackend("synthetic success", () => future, path => { File.WriteAllBytes(path, [1]); return true; });
        Require(newest is not null && File.Exists(newest), "Pruning must never delete the dump it was run for, even when older dumps carry later timestamps.");
        Require(DumpNames(future).Length == 10, $"Pruning must still keep exactly 10 dumps, found {DumpNames(future).Length}.");
    });

    // Two crash handlers on different threads: the first writer holds the native write until
    // the second thread is seen blocked outside it (or, without the guard, has come in).
    private static void SerializedWriters() => WithFixture(root =>
    {
        var dir = Path.Combine(root, "dumps");
        var inside = 0;
        var maxInside = 0;
        var secondSeenWaiting = false;
        Thread? second = null;
        using var firstInside = new ManualResetEventSlim();
        bool Write(string path, bool first)
        {
            var now = Interlocked.Increment(ref inside);
            lock (firstInside) maxInside = Math.Max(maxInside, now);
            try
            {
                if (first)
                {
                    firstInside.Set();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (watch.ElapsedMilliseconds < 5000 && Volatile.Read(ref inside) < 2)
                    {
                        if (Volatile.Read(ref second) is { } peer && (peer.ThreadState & ThreadState.WaitSleepJoin) != 0) { secondSeenWaiting = true; break; }
                        Thread.Sleep(5);
                    }
                    Thread.Sleep(50); // Without the guard the second writer would come in now.
                }
                File.WriteAllBytes(path, [first ? (byte)1 : (byte)2]);
                return true;
            }
            finally { Interlocked.Decrement(ref inside); }
        }
        string? firstPath = null, secondPath = null;
        var firstThread = new Thread(() => firstPath = CrashDumpWriter.WriteDumpWithBackend("synthetic first handler", () => dir, path => Write(path, true))) { IsBackground = true };
        firstThread.Start();
        Require(firstInside.Wait(5000), "The first writer must start.");
        var secondThread = new Thread(() => secondPath = CrashDumpWriter.WriteDumpWithBackend("synthetic second handler", () => dir, path => Write(path, false))) { IsBackground = true };
        Volatile.Write(ref second, secondThread);
        secondThread.Start();
        Require(firstThread.Join(10000) && secondThread.Join(10000), "Both crash handlers must finish.");
        Require(maxInside == 1, $"Two threads were inside the native dump writer at once (max {maxInside}).");
        Require(secondSeenWaiting, "The second handler must have been seen waiting while the first was writing.");
        Require(firstPath is not null && secondPath is not null && firstPath != secondPath, "Both serialized dumps must be written under their own names.");
        Require(Count == 2, $"Both successful writes must keep their reservations, count={Count}.");
    });

    // A writer that hangs must not stop another thread's crash report. Serialization itself is
    // the previous case's property; this one only requires that no handler waits past its
    // bound and that a handler which gives up returns its reservation.
    private static void BoundedWriterWait() => WithFixture(root =>
    {
        var dir = Path.Combine(root, "dumps");
        using var firstInside = new ManualResetEventSlim();
        using var waiterDone = new ManualResetEventSlim();
        string? firstPath = null;
        var firstThread = new Thread(() => firstPath = CrashDumpWriter.WriteDumpWithBackend("synthetic hung writer", () => dir, path =>
        {
            firstInside.Set();
            waiterDone.Wait(5000);
            File.WriteAllBytes(path, [1]);
            return true;
        })) { IsBackground = true };
        firstThread.Start();
        Require(firstInside.Wait(5000), "The first writer must start.");
        var waiterRan = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var waiterPath = CrashDumpWriter.WriteDumpWithBackend("synthetic waiting handler", () => dir, _ => { waiterRan = true; return true; },
            writerWait: TimeSpan.FromMilliseconds(200));
        var waited = watch.ElapsedMilliseconds;
        var countAfterGivingUp = Count;
        waiterDone.Set();
        Require(firstThread.Join(10000), "The held writer must finish.");
        Require(waited < 2000, $"A handler must not wait for a hung writer past its bound, waited {waited} ms.");
        if (waiterPath is null)
        {
            Require(!waiterRan && waited >= 150, $"A handler that gives up must do so at its bound, waited {waited} ms.");
            Require(countAfterGivingUp == 1, $"The handler that gave up must return its reservation, count={countAfterGivingUp}.");
        }
        Require(firstPath is not null, "The held writer must still finish its dump.");
        var later = CrashDumpWriter.WriteDumpWithBackend("synthetic later handler", () => dir, path => { File.WriteAllBytes(path, [2]); return true; },
            writerWait: TimeSpan.FromMilliseconds(200));
        Require(later is not null, "The guard must be free again once the writer finishes.");
    });

    // A fixed clock gives every attempt the same millisecond stamp.
    private static void SameInstantNames() => WithFixture(root =>
    {
        var dir = Path.Combine(root, "dumps");
        var instant = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Local);
        string? Write(string content, bool ok) => CrashDumpWriter.WriteDumpWithBackend("synthetic same instant", () => dir, path =>
        {
            File.WriteAllText(path, content);
            return ok;
        }, clock: () => instant);
        var first = Write("first", true);
        var second = Write("second", true);
        Require(first is not null && second is not null && first != second, $"Two dumps in the same millisecond must get different names, got '{first}' and '{second}'.");
        Require(File.ReadAllText(first!) == "first" && File.ReadAllText(second!) == "second", "A dump in the same millisecond must not overwrite the previous one.");
        Require(Write("third", false) is null, "The failing same-instant attempt must report failure.");
        Require(File.ReadAllText(first!) == "first" && File.ReadAllText(second!) == "second",
            "Cleaning up a failed same-instant attempt must leave the other dumps alone.");
        Require(DumpNames(dir).Length == 2, $"The failed same-instant attempt must delete its own file, found {DumpNames(dir).Length} dumps.");
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
