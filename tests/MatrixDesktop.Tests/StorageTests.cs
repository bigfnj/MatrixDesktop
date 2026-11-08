using MatrixDesktopConfigurator;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatrixDesktop.Tests;

// These exercise the real StorageService, which resolves its own path and therefore writes
// into this test project's output folder. Every case cleans up after itself so a later run
// starts from the same state. Recovery fixtures use the internal explicit-path constructor
// to exercise synthetic stores without touching a live configurator store.
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
        ("Null preset records and values recover without changing valid data or backups", NullPresetContainersRecover),
        ("Renaming a preset changes only its name", RenameChangesOnlyName),
        ("The scratch draft survives choosing a preset and a relaunch", ScratchDraftSurvivesPresetChoice),
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

    private static void NullPresetContainersRecover()
    {
        // Killing mutations: omit null-record removal, or omit null-Values repair.
        // Both are reached through live and backup loads before the real seeder runs.
        const string json = """
            {
              "schemaVersion": 1, "presetSeedVersion": 5, "selectedPresetId": "kept-id", "uiTheme": "light",
              "lastDraft": { "scene": "uc3d", "uc3dControls": "original", "uc3dAmount": 731 },
              "userPresets": [null,
                { "id": "kept-id", "name": "My exact preset", "createdUtc": "2026-01-02T03:04:05Z", "updatedUtc": "2026-02-03T04:05:06Z",
                  "values": { "scene": "matrix", "numColumns": 137, "density": 2.345, "customExtension": { "kept": [1, "literal", true] } } },
                { "id": "empty-id", "name": "Keep my identity", "values": null }
              ]
            }
            """;
        var expected = JsonNode.Parse(json)!.AsObject();
        foreach (var fromBackup in new[] { false, true })
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "synthetic-storage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var storage = new StorageService(Path.Combine(directory, "presets.json"));
            try
            {
                File.WriteAllText(storage.BackupPath, json);
                if (!fromBackup) File.WriteAllText(storage.StoragePath, json);
                var loaded = storage.Load();
                Check.Equal(2, loaded.UserPresets.Count, "null list entries contain no preset and must not reach the seeder");
                Check.Equal("kept-id", loaded.UserPresets[0].Id, "the surviving preset keeps its original identity");
                Check.Equal("My exact preset", loaded.UserPresets[0].Name, "the surviving preset keeps its original name");
                Check.True(JsonNode.DeepEquals(expected["userPresets"]![1]!["values"], loaded.UserPresets[0].Values), "all valid and extension values must remain exact");
                Check.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), loaded.UserPresets[0].CreatedUtc, "valid creation time must remain exact");
                Check.Equal(DateTimeOffset.Parse("2026-02-03T04:05:06Z"), loaded.UserPresets[0].UpdatedUtc, "valid modification time must remain exact");
                Check.Equal("empty-id", loaded.UserPresets[1].Id, "a missing Values object must not discard the preset record");
                Check.Equal("Keep my identity", loaded.UserPresets[1].Name, "the repaired preset keeps its name");
                Check.True(loaded.UserPresets[1].Values is { Count: 0 }, "null values must become an empty object before scene normalization");
                Check.True(JsonNode.DeepEquals(expected["lastDraft"], loaded.LastDraft), "the active draft must remain exact");
                Check.Equal("kept-id", loaded.SelectedPresetId!, "the selected surviving preset must stay selected");
                var metadata = ArgumentCatalog.Create();
                var builder = new CommandBuilder(metadata);
                _ = PresetSeeder.Seed(loaded, builder, new ArgumentImporter(metadata));
                _ = builder.BuildCommand(loaded.UserPresets[1].Values, false, false);
                Check.Equal(json, File.ReadAllText(storage.BackupPath), "loading and seeding must leave original backup bytes untouched");
                if (!fromBackup) Check.Equal(json, File.ReadAllText(storage.StoragePath), "normalization must not rewrite the live file while loading");
                storage.Save(loaded);
                var roundTrip = storage.Load();
                Check.Equal(loaded.UserPresets.Count, roundTrip.UserPresets.Count, "repaired state and newly introduced starters must remain usable after saving");
                Check.True(roundTrip.UserPresets.Any(preset => preset.Id == "kept-id"), "the original real preset must remain present after seeding");
                Check.True(JsonNode.DeepEquals(loaded.UserPresets[0].Values, roundTrip.UserPresets[0].Values), "saving must preserve the real preset's custom data");
            }
            finally
            {
                var fixtureDirectory = Path.GetFullPath(directory);
                var fixtureParent = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Check.True(string.Equals(Path.GetDirectoryName(fixtureDirectory), fixtureParent, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(fixtureDirectory).StartsWith("synthetic-storage-", StringComparison.Ordinal),
                    "cleanup must stay inside this test's owned synthetic fixture directory");
                foreach (var path in new[] { storage.StoragePath, storage.BackupPath, storage.StoragePath + ".tmp" })
                    if (File.Exists(path)) File.Delete(path);
                foreach (var quarantine in Directory.EnumerateFiles(fixtureDirectory, "presets.json.unreadable-*", SearchOption.TopDirectoryOnly))
                    File.Delete(quarantine);
                Directory.Delete(fixtureDirectory);
            }
        }
    }

    private static void RenameChangesOnlyName()
    {
        // AUD-02. Killing mutation: assign Values from LastDraft inside Rename; the saved
        // values assertion fails.
        var created = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var now = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
        var state = new ConfiguratorState { SelectedPresetId = "night", LastDraft = new JsonObject { ["effect"] = "stripes" } };
        state.UserPresets.Add(new UserPreset { Id = "night", Name = "Night", CreatedUtc = created, UpdatedUtc = created, Values = new JsonObject { ["effect"] = "palette" } });
        state.UserPresets.Add(new UserPreset { Id = "dawn", Name = "Dawn", CreatedUtc = created, UpdatedUtc = created, Values = new JsonObject { ["effect"] = "plain" } });
        UserPreset Rename(string json)
        {
            using var request = JsonDocument.Parse(json);
            return PresetOperations.Rename(state, request.RootElement, now);
        }

        var renamed = Rename("""{"id":"night","name":"  Midnight  "}""");
        Check.True(ReferenceEquals(state.UserPresets[0], renamed), "the reply carries the renamed preset itself");
        Check.Equal("Midnight", renamed.Name, "the new name is applied, trimmed");
        Check.Equal("palette", renamed.Values["effect"]!.GetValue<string>(), "a rename must keep the preset's saved values, not commit the working draft");
        Check.Equal("stripes", state.LastDraft["effect"]!.GetValue<string>(), "the unsaved working draft stays the working draft");
        Check.Equal("night", state.SelectedPresetId!, "the selection does not change");
        Check.Equal(now, renamed.UpdatedUtc, "the modification time records the rename");
        Check.Equal(created, renamed.CreatedUtc, "the creation time does not change");

        Check.Equal("Midnight", Rename("""{"id":"night","name":"   "}""").Name, "a blank name keeps the old one");
        var other = Rename("""{"id":"DAWN","name":"Sunrise"}""");
        Check.True(other.Name == "Sunrise" && other.Values["effect"]!.GetValue<string>() == "plain" && state.SelectedPresetId == "night",
            "renaming another preset (ids compare case-insensitively) touches only that preset and keeps the selection");

        var refused = false;
        try { Rename("""{"id":"gone","name":"Ghost"}"""); }
        catch (InvalidOperationException) { refused = true; }
        Check.True(refused && state.UserPresets.Count == 2, "renaming a preset that no longer exists is an error, never a new preset");
    }

    private static void ScratchDraftSurvivesPresetChoice()
    {
        // AUD-17. Killing mutation: delete the explicit ScratchDraft assignment in
        // PresetOperations.SaveDraft; the scratch draft the page sends is then lost.
        var state = new ConfiguratorState();
        void Save(string json)
        {
            using var request = JsonDocument.Parse(json);
            PresetOperations.SaveDraft(state, request.RootElement);
        }
        static int Columns(JsonObject? draft) => draft?["numColumns"]?.GetValue<int>() ?? -1;

        Save("""{"draft":{"numColumns":50},"selectedPresetId":"alpha","scratchDraft":{"numColumns":33}}""");
        Check.Equal(33, Columns(state.ScratchDraft), "the scratch draft the page sends while a preset is shown must be stored");
        Check.True(Columns(state.LastDraft) == 50 && state.SelectedPresetId == "alpha", "the working draft and the selection are saved as before");
        Save("""{"draft":{"numColumns":51},"selectedPresetId":"alpha"}""");
        Check.Equal(33, Columns(state.ScratchDraft), "a linked save without a scratch draft (older page, close snapshot) keeps the stored one");
        Save("""{"draft":{"numColumns":34},"selectedPresetId":null}""");
        Check.True(Columns(state.ScratchDraft) == 34 && !ReferenceEquals(state.ScratchDraft, state.LastDraft),
            "an unlinked save without a scratch draft keeps its own copy of the draft as the scratch draft");

        var directory = Path.Combine(AppContext.BaseDirectory, "synthetic-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storage = new StorageService(Path.Combine(directory, "presets.json"));
        try
        {
            storage.Save(state);
            Check.Contains(File.ReadAllText(storage.StoragePath), "\"scratchDraft\"", "the scratch draft is stored under the camelCase key loadState hands the page");
            Check.Equal(34, Columns(storage.Load().ScratchDraft), "the scratch draft survives a relaunch");
            storage.Save(new ConfiguratorState());
            Check.DoesNotContain(File.ReadAllText(storage.StoragePath), "scratchDraft", "a store without a scratch draft keeps the old shape");
            Check.True(storage.Load().ScratchDraft is null, "an old store loads without one, so the page falls back to LastDraft");
        }
        finally
        {
            foreach (var path in new[] { storage.StoragePath, storage.BackupPath, storage.StoragePath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
