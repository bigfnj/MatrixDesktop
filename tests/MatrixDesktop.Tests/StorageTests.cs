using MatrixDesktopConfigurator;

namespace MatrixDesktop.Tests;

// These exercise the real StorageService, which resolves its own path and therefore writes
// into this test project's output folder. Every case cleans up after itself so a later run
// starts from the same state. There is no seam to inject a path through, which is itself
// worth knowing: the production type decides where it writes.
internal static class StorageTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("A save followed by a load round trips the state", RoundTrip),
        ("Probing for writability leaves no zero byte file behind", ProbeLeavesNothing),
        ("A save keeps the previous contents as a backup", SaveKeepsBackup),
        ("A truncated live file recovers from the backup", TornWriteRecovers),
        ("An unreadable live file is moved aside rather than overwritten", UnreadableFileIsQuarantined),
        ("An empty live file yields fresh state rather than an error", EmptyFileIsNotCorruption),
    ];

    private static void Cleanup(StorageService storage)
    {
        foreach (var path in new[] { storage.StoragePath, storage.BackupPath, storage.StoragePath + ".tmp" })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        var dir = Path.GetDirectoryName(storage.StoragePath);
        if (dir is null) return;
        foreach (var stray in Directory.EnumerateFiles(dir, "*.unreadable-*"))
        {
            try { File.Delete(stray); } catch { }
        }
        foreach (var stray in Directory.EnumerateFiles(dir, ".write-probe-*"))
        {
            try { File.Delete(stray); } catch { }
        }
    }

    private static ConfiguratorState StateWith(string presetName)
    {
        var state = new ConfiguratorState { UiTheme = "light" };
        state.UserPresets.Add(new UserPreset { Id = "id-1", Name = presetName });
        return state;
    }

    private static void RoundTrip()
    {
        var storage = new StorageService();
        try
        {
            storage.Save(StateWith("keeper"));
            var loaded = storage.Load();
            Check.Equal(1, loaded.UserPresets.Count, "a saved preset must come back");
            Check.Equal("keeper", loaded.UserPresets[0].Name, "with its name intact");
            Check.Equal("light", loaded.UiTheme, "and the theme preference, which is persisted alongside");
        }
        finally
        {
            Cleanup(storage);
        }
    }

    private static void ProbeLeavesNothing()
    {
        // MD-06. The writability probe used FileMode.OpenOrCreate, so constructing the
        // service created a zero-byte presets file, which Load then reported as an ERROR on
        // every first run.
        var first = new StorageService();
        Cleanup(first);

        var storage = new StorageService();
        try
        {
            Check.False(File.Exists(storage.StoragePath),
                "constructing the service must not create the presets file; that is what caused the first-run ERROR");

            var dir = Path.GetDirectoryName(storage.StoragePath)!;
            Check.Equal(0, Directory.GetFiles(dir, ".write-probe-*").Length,
                "the temp probe must be deleted again, not left in the publish folder");
        }
        finally
        {
            Cleanup(storage);
        }
    }

    private static void SaveKeepsBackup()
    {
        var storage = new StorageService();
        try
        {
            storage.Save(StateWith("first"));
            storage.Save(StateWith("second"));

            Check.True(File.Exists(storage.BackupPath), "the second save must rotate the first into the backup");
            Check.Contains(File.ReadAllText(storage.BackupPath), "first", "the backup holds the PREVIOUS contents");
            Check.Contains(File.ReadAllText(storage.StoragePath), "second", "and the live file holds the newest");
        }
        finally
        {
            Cleanup(storage);
        }
    }

    private static void TornWriteRecovers()
    {
        var storage = new StorageService();
        try
        {
            storage.Save(StateWith("good"));
            storage.Save(StateWith("newer"));

            // Simulate a process death part way through a write.
            var full = File.ReadAllText(storage.StoragePath);
            File.WriteAllText(storage.StoragePath, full[..(full.Length / 2)]);

            var loaded = storage.Load();
            Check.Equal(1, loaded.UserPresets.Count, "a torn live file must not lose every preset");
            Check.Equal("good", loaded.UserPresets[0].Name,
                "the backup holds the previous good copy, which is what recovery means here");
        }
        finally
        {
            Cleanup(storage);
        }
    }

    private static void UnreadableFileIsQuarantined()
    {
        var storage = new StorageService();
        try
        {
            File.WriteAllText(storage.StoragePath, "{ this is not json");
            _ = storage.Load();

            var dir = Path.GetDirectoryName(storage.StoragePath)!;
            var quarantined = Directory.GetFiles(dir, "*.unreadable-*");
            Check.True(quarantined.Length >= 1,
                "the unreadable file must be moved aside; leaving it meant the next save destroyed the evidence");
            Check.Contains(File.ReadAllText(quarantined[0]), "not json", "and it must still contain what could not be parsed");
            Check.False(File.Exists(storage.StoragePath), "the live path is cleared so the next save starts clean");
        }
        finally
        {
            Cleanup(storage);
        }
    }

    private static void EmptyFileIsNotCorruption()
    {
        var storage = new StorageService();
        try
        {
            File.WriteAllText(storage.StoragePath, string.Empty);
            var loaded = storage.Load();
            Check.Equal(0, loaded.UserPresets.Count, "an empty file yields fresh state");

            var dir = Path.GetDirectoryName(storage.StoragePath)!;
            Check.Equal(0, Directory.GetFiles(dir, "*.unreadable-*").Length,
                "an empty file is a first-run artefact, not corruption, so nothing is quarantined");
        }
        finally
        {
            Cleanup(storage);
        }
    }
}
