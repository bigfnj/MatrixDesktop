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
        ("Two launches probing a missing store at once both resolve the portable store", OverlappingLaunchesAgree),
        ("Probing an existing store neither yields to nor disturbs a launch reading it", ProbingLeavesReadersAlone),
        ("A portable store that refuses writes falls back to AppData and says so", DeniedStoreFallsBack),
        ("A portable folder that refuses new files falls back to AppData", DeniedFolderFallsBack),
        ("A launch overlapping the owner's Save resolves the portable store", SaveGapResolvesPortable),
        ("A portable store at or past MAX_PATH still resolves portable", LongPathStoreResolvesPortable),
        ("A portable store whose ACL keeps write attributes but refuses Save falls back to AppData", SaveRefusingAclFallsBack),
        ("A store whose ACL check cannot run in full keeps the kernel's answer and says so", UnevaluableAclKeepsStoreAndSaysSo),
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
        foreach (var stray in Directory.EnumerateFiles(dir, ".write-probe*"))
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
        // R2-17: '.write-probe*' covers the bare name and per-attempt names. The old
        // '.write-probe-*' never matched the bare '.write-probe', so this check could not fail.
        // Killing mutation: stop deleting the probe; the probe count below fails.
        var first = new StorageService();
        Cleanup(first);

        var storage = new StorageService();
        try
        {
            Check.False(File.Exists(storage.StoragePath),
                "constructing the service must not create the presets file; that is what caused the first-run ERROR");

            var dir = Path.GetDirectoryName(storage.StoragePath)!;
            Check.Equal(0, Directory.GetFiles(dir, ".write-probe*").Length,
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

    // R2-18 (MDR-04): store resolution when launches overlap. These drive the real resolver
    // against owned portable and AppData roots, so they never probe this harness's own
    // publish folder, and no case ever creates the AppData side.
    private static string NewResolverRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "synthetic-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "portable"));
        return root;
    }

    private static void DeleteResolverRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var parent = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Check.True(string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("synthetic-storage-", StringComparison.Ordinal),
            "cleanup must stay inside this test's owned synthetic fixture directory");
        Directory.Delete(full, recursive: true);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DenyCurrentUser(string path, System.Security.AccessControl.FileSystemRights rights, bool deny)
    {
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!, rights, System.Security.AccessControl.AccessControlType.Deny);
        if (Directory.Exists(path))
        {
            var folder = new DirectoryInfo(path);
            var acl = folder.GetAccessControl();
            if (deny) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
            folder.SetAccessControl(acl);
        }
        else
        {
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            if (deny) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
            file.SetAccessControl(acl);
        }
    }

    private static void OverlappingLaunchesAgree()
    {
        // The second resolution runs while the first one's probe is held, the overlap that two
        // launches started together produce. Killing mutation: probe the fixed '.write-probe'
        // with FileMode.Create and FileShare.None again; the overlapping launch then resolves
        // AppData and takes a second store's guard.
        var root = NewResolverRoot();
        try
        {
            var portable = Path.Combine(root, "portable", "presets.json");
            var appData = Path.Combine(root, "appdata", "presets.json");
            (string Path, bool Portable) second = ("(never resolved)", false);
            string? firstProbe = null, secondProbe = null;
            var first = StorageService.ResolveStoragePath(portable, appData, whileProbeOpen: probe =>
            {
                firstProbe = probe;
                second = StorageService.ResolveStoragePath(portable, appData, whileProbeOpen: inner => secondProbe = inner);
            });

            Check.True(first == (portable, true) && second == (portable, true),
                $"two launches resolving a missing store at once must both choose the portable store; got '{first.Path}' and '{second.Path}'");
            using (var owner = SingleInstanceGuard.TryAcquire(first.Path))
            using (var refused = SingleInstanceGuard.TryAcquire(second.Path))
                Check.True(owner is not null && refused is null, "so exactly one of them takes the store's guard");
            Check.True(firstProbe is not null && secondProbe is not null && firstProbe != secondProbe,
                "the second launch must open its own probe while the first launch's probe is held");
            Check.False(File.Exists(portable), "probing must not create the store");
            Check.False(Directory.Exists(Path.GetDirectoryName(appData)), "and must not touch the AppData store");
        }
        finally
        {
            DeleteResolverRoot(root);
        }
    }

    private static void ProbingLeavesReadersAlone()
    {
        // The probe used to open an existing store ReadWrite. Killing mutations: restore that
        // open and hold it while the probe runs (the owner's load below fails and returns the
        // backup); or restore it closed before the probe (a launch probing while the store is
        // being read then resolves AppData).
        var root = NewResolverRoot();
        try
        {
            var portable = Path.Combine(root, "portable", "presets.json");
            var appData = Path.Combine(root, "appdata", "presets.json");
            var owner = new StorageService(portable);
            owner.Save(StateWith("previous"));
            owner.Save(StateWith("live"));

            // The owning configurator loads while a second launch's probe is held.
            ConfiguratorState? loaded = null;
            var probing = StorageService.ResolveStoragePath(portable, appData, whileProbeOpen: _ => loaded = owner.Load());
            Check.Equal("live", loaded?.UserPresets.FirstOrDefault()?.Name ?? "(no load ran)",
                "the owner's load during another launch's probe must return the live store, not the backup");
            Check.Equal(0, Directory.GetFiles(Path.GetDirectoryName(portable)!, "*.unreadable-*").Length,
                "and must not move the live store aside as unreadable");
            Check.True(probing == (portable, true), "the probing launch still resolves the portable store");

            // A second launch probes while the store is open: the way Load's File.ReadAllText
            // opens it, and exclusively, as a sync or backup tool may hold it.
            foreach (var share in new[] { FileShare.Read, FileShare.None })
            {
                using var reader = new FileStream(portable, FileMode.Open, FileAccess.Read, share);
                var during = StorageService.ResolveStoragePath(portable, appData);
                Check.True(during == (portable, true),
                    $"a launch probing while the store is open with FileShare.{share} must still resolve the portable store; got '{during.Path}'");
            }
            Check.False(Directory.Exists(Path.GetDirectoryName(appData)), "the AppData store is never touched");
        }
        finally
        {
            DeleteResolverRoot(root);
        }
    }

    private static void DeniedStoreFallsBack()
    {
        // Only a real refusal falls back, and a fallback that hides existing portable presets
        // is reported. Killing mutations: drop the read-only check, drop the ACL check, or drop
        // the warning; each fails one leg below.
        var root = NewResolverRoot();
        var portable = Path.Combine(root, "portable", "presets.json");
        var appData = Path.Combine(root, "appdata", "presets.json");
        var warnings = new List<string>();
        try
        {
            File.WriteAllText(portable, "{}");
            Check.True(StorageService.ResolveStoragePath(portable, appData, warn: warnings.Add) == (portable, true) && warnings.Count == 0,
                "a writable store resolves to itself without a warning");

            File.SetAttributes(portable, FileAttributes.ReadOnly);
            var readOnly = StorageService.ResolveStoragePath(portable, appData, warn: warnings.Add);
            File.SetAttributes(portable, FileAttributes.Normal);
            Check.True(readOnly == (appData, false), "a read-only portable store falls back to AppData");
            Check.True(warnings.Count == 1 && warnings[0].Contains(portable, StringComparison.Ordinal) && warnings[0].Contains(appData, StringComparison.Ordinal),
                "and the fallback is reported, naming the store it cannot use and the one it uses");

            if (OperatingSystem.IsWindows())
            {
                // The folder still accepts new files; only the store's ACL refuses writes.
                (string Path, bool Portable) denied;
                DenyCurrentUser(portable, System.Security.AccessControl.FileSystemRights.Write, deny: true);
                try { denied = StorageService.ResolveStoragePath(portable, appData, warn: warnings.Add); }
                finally { DenyCurrentUser(portable, System.Security.AccessControl.FileSystemRights.Write, deny: false); }
                Check.True(denied == (appData, false), "a store whose ACL refuses writes falls back to AppData although its folder accepts new files");
                Check.Equal(2, warnings.Count, "and that fallback is reported too");
            }
            else
            {
                Console.WriteLine("NOTE  ACL leg of the denied-store case UNCHECKED: not Windows");
            }
        }
        finally
        {
            if (File.Exists(portable)) File.SetAttributes(portable, FileAttributes.Normal);
            DeleteResolverRoot(root);
        }
    }

    private static void DeniedFolderFallsBack()
    {
        // MDR-04 asked for a genuinely denied root. Killing mutation: report the folder
        // writable when the probe cannot be created; this case then resolves the portable store.
        var root = NewResolverRoot();
        var folder = Path.Combine(root, "portable");
        var portable = Path.Combine(folder, "presets.json");
        var appData = Path.Combine(root, "appdata", "presets.json");
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("NOTE  denied-folder case UNCHECKED: not Windows");
            DeleteResolverRoot(root);
            return;
        }

        var warnings = new List<string>();
        try
        {
            var rights = System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.WriteData;
            (string Path, bool Portable) resolved;
            DenyCurrentUser(folder, rights, deny: true);
            try { resolved = StorageService.ResolveStoragePath(portable, appData, warn: warnings.Add); }
            finally { DenyCurrentUser(folder, rights, deny: false); }
            Check.True(resolved == (appData, false), "a portable folder that refuses new files falls back to AppData");
            Check.Equal(0, warnings.Count, "with no portable store to hide, this is the installed-copy mode and is not reported");
            Check.Equal(0, Directory.GetFiles(folder).Length, "and the refused probe leaves nothing behind");
        }
        finally
        {
            DeleteResolverRoot(root);
        }
    }

    private static void SaveGapResolvesPortable()
    {
        // R3-10. Save swaps an existing store in with File.Replace, which renames the live store
        // to .bak and then the new copy onto its name, so a launch can find the name gone after
        // File.Exists said it was there. Each leg holds the name absent across one check.
        // Killing mutations: let GetAttributes' FileNotFoundException reach the outer catch (the
        // "exists" leg resolves AppData); treat every failed ACL open as a refusal (the
        // "attributes" leg resolves AppData).
        var root = NewResolverRoot();
        try
        {
            var portable = Path.Combine(root, "portable", "presets.json");
            var renamed = portable + ".bak";
            var appData = Path.Combine(root, "appdata", "presets.json");
            new StorageService(portable).Save(StateWith("live"));

            foreach (var gapAt in new[] { "exists", "attributes" })
            {
                var warnings = new List<string>();
                var steps = new List<string>();
                var restored = false;
                var resolved = StorageService.ResolveStoragePath(portable, appData,
                    whileProbeOpen: _ =>
                    {
                        // The owner's second rename lands while this launch probes the folder.
                        if (!File.Exists(renamed)) return;
                        File.Move(renamed, portable);
                        restored = true;
                    },
                    warn: warnings.Add,
                    afterStoreStep: step =>
                    {
                        steps.Add(step);
                        if (step == gapAt) File.Move(portable, renamed);
                    });
                if (File.Exists(renamed)) File.Move(renamed, portable);

                Check.True(steps.Contains(gapAt), $"the '{gapAt}' leg must reach its step with the store present; steps were [{string.Join(", ", steps)}]");
                Check.True(resolved == (portable, true) && warnings.Count == 0,
                    $"a launch that finds the store's name gone after '{gapAt}' must resolve the portable store without a warning; got '{resolved.Path}' with {warnings.Count} warning(s)");
                Check.True(restored, $"and its folder probe runs, which is where the '{gapAt}' leg puts the name back");
            }
            Check.False(Directory.Exists(Path.GetDirectoryName(appData)), "the AppData store is never touched");
        }
        finally
        {
            DeleteResolverRoot(root);
        }
    }

    private static void LongPathStoreResolvesPortable()
    {
        // R3-11. The ACL check opens the store with a raw CreateFileW, which needs the
        // extended-length prefix that .NET's file APIs add for themselves; without it a store
        // path of MAX_PATH or more reads as not found. Killing mutation: pass the plain path to
        // CreateFileW again; the denied 270-character leg then resolves portable because the
        // ACL is never consulted.
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("NOTE  long-path store case UNCHECKED: not Windows");
            return;
        }

        Check.Equal(@"\\?\C:\a\presets.json", StorageService.ExtendedLengthPath(@"C:\a\presets.json"), "a drive path gains the extended-length prefix");
        Check.Equal(@"\\?\UNC\server\share\presets.json", StorageService.ExtendedLengthPath(@"\\server\share\presets.json"), "a UNC path gains the extended UNC prefix");
        Check.Equal(@"\\?\C:\a\presets.json", StorageService.ExtendedLengthPath(@"\\?\C:\a\presets.json"), "an extended path is kept as it is");

        var root = NewResolverRoot();
        var appData = Path.Combine(root, "appdata", "presets.json");
        try
        {
            foreach (var length in new[] { 250, 270 })
            {
                var portable = StoreOfLength(root, length);
                Check.Equal(length, portable.Length, "the padded store path has the length this leg names");
                new StorageService(portable).Save(StateWith("long"));
                var warnings = new List<string>();
                var resolved = StorageService.ResolveStoragePath(portable, appData, warn: warnings.Add);
                Check.True(resolved == (portable, true) && warnings.Count == 0,
                    $"a writable {length}-character store must resolve to itself without a warning; got '{resolved.Path}' with {warnings.Count} warning(s)");
            }

            var longest = StoreOfLength(root, 270);
            Check.True(longest.Length >= 260, "the denied leg's store must be at least MAX_PATH long, or it cannot catch a missing prefix");
            var deniedWarnings = new List<string>();
            (string Path, bool Portable) refused;
            DenyCurrentUser(longest, System.Security.AccessControl.FileSystemRights.Write, deny: true);
            try { refused = StorageService.ResolveStoragePath(longest, appData, warn: deniedWarnings.Add); }
            finally { DenyCurrentUser(longest, System.Security.AccessControl.FileSystemRights.Write, deny: false); }
            Check.True(refused == (appData, false), $"a {longest.Length}-character store whose ACL refuses writes must fall back to AppData; got '{refused.Path}'");
            Check.True(deniedWarnings.Count == 1 && deniedWarnings[0].Contains(longest, StringComparison.Ordinal),
                "and the fallback is reported, naming the long store");
            Check.False(Directory.Exists(Path.GetDirectoryName(appData)), "the AppData store is never touched");
        }
        finally
        {
            DeleteResolverRoot(root);
        }
    }

    // A store path of exactly `length` characters under the owned root, padded with nested
    // folders of at most 100 characters each, created on disk.
    private static string StoreOfLength(string root, int length)
    {
        const string name = "presets.json";
        var folder = Path.Combine(root, "portable");
        var remaining = length - name.Length - 1 - folder.Length;
        Check.True(remaining >= 2, $"the owned root '{root}' is too deep for a {length}-character store");
        var segments = (remaining + 100) / 101;
        var characters = remaining - segments;
        for (var i = 0; i < segments; i++)
        {
            var size = characters / segments + (i < characters % segments ? 1 : 0);
            folder = Path.Combine(folder, (length + new string('p', size))[..size]);
        }
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }

    private static void SaveRefusingAclFallsBack()
    {
        // R3-12 (MD517-01). R2-18 tested an existing store with a FILE_WRITE_ATTRIBUTES open
        // alone, so an ACL that keeps that right but refuses the data rights or DELETE passed,
        // and every Save then failed. Each leg keeps FILE_WRITE_ATTRIBUTES. Killing mutation:
        // revert to the FILE_WRITE_ATTRIBUTES-only check; the three refusing legs resolve
        // portable. ProbingLeavesReadersAlone guards the wrong fix (opening for those rights).
        // The folder's FILE_DELETE_CHILD is the kernel's answer: report every folder as granting
        // it and the Write-without-Modify leg resolves portable; as refusing it and the last leg
        // resolves AppData. No leg may report the check degraded on this local volume.
        // Each leg also runs Save under the same ACL, so the refusals are Save's own. The third
        // leg is the exception: File.Replace only renames, so Save gets past a store that
        // refuses writing data, but such a store is not written, as with DeniedStoreFallsBack's
        // Write deny.
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("NOTE  Save-refusing ACL case UNCHECKED: not Windows");
            return;
        }

        var root = NewResolverRoot();
        var folder = Path.Combine(root, "portable");
        var portable = Path.Combine(folder, "presets.json");
        var appData = Path.Combine(root, "appdata", "presets.json");
        new StorageService(portable).Save(StateWith("live"));

        const System.Security.AccessControl.FileSystemRights data = System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData;
        const System.Security.AccessControl.FileSystemRights delete = System.Security.AccessControl.FileSystemRights.Delete;
        const System.Security.AccessControl.FileSystemRights deleteChild = System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles;
        var legs = new (string Name, System.Security.AccessControl.FileSystemRights Store, bool DenyFolderDeleteChild, bool SaveWorks, bool Portable)[]
        {
            ("data, append and delete denied, folder refuses deleting children", data | delete, true, false, false),
            ("delete denied, folder refuses deleting children (Write without Modify)", delete, true, false, false),
            ("data and append denied, delete allowed", data, false, true, false),
            ("delete denied, folder grants deleting children", delete, false, true, true),
        };
        try
        {
            foreach (var leg in legs)
            {
                var warnings = new List<string>();
                var degraded = new List<string>();
                (string Path, bool Portable) resolved;
                Exception? saveFailure = null;
                SetCurrentUserRule(folder, deleteChild, leg.DenyFolderDeleteChild ? System.Security.AccessControl.AccessControlType.Deny : System.Security.AccessControl.AccessControlType.Allow, add: true);
                DenyCurrentUser(portable, leg.Store, deny: true);
                try
                {
                    resolved = StorageService.ResolveStoragePath(portable, appData, warn: warnings.Add, degraded: degraded.Add);
                    try { new StorageService(portable).Save(StateWith("saved under " + leg.Name)); }
                    catch (Exception ex) { saveFailure = ex; }
                }
                finally
                {
                    ClearCurrentUserRules(folder);
                }

                var expected = leg.Portable ? (portable, true) : (appData, false);
                Check.True(resolved == expected, $"a store with {leg.Name} must resolve '{expected.Item1}'; got '{resolved.Path}'");
                Check.True(leg.Portable ? warnings.Count == 0 : warnings.Count == 1 && warnings[0].Contains(portable, StringComparison.Ordinal) && warnings[0].Contains(appData, StringComparison.Ordinal),
                    $"a fallback from a store with {leg.Name} is reported naming both stores, and keeping the store is not; got {warnings.Count} warning(s)");
                Check.True(leg.SaveWorks == (saveFailure is null),
                    $"Save under {leg.Name} {(leg.SaveWorks ? "succeeds" : "fails")}; got {saveFailure?.GetType().Name ?? "success"}");
                Check.True(degraded.Count == 0,
                    $"on a local volume the ACL check of a store with {leg.Name} runs in full, so nothing reports it degraded; got [{string.Join(" | ", degraded)}]");
            }
            Check.False(Directory.Exists(Path.GetDirectoryName(appData)), "the AppData store is never touched");
        }
        finally
        {
            ClearCurrentUserRules(folder);
            DeleteResolverRoot(root);
        }
    }

    private static void UnevaluableAclKeepsStoreAndSaysSo()
    {
        // R3-12. Where the ACL check of an existing store cannot run in full, the kernel's
        // FILE_WRITE_ATTRIBUTES answer keeps the store as it did before R3-12, and that must be
        // said on every resolution: the store's DACL could not be read or evaluated; the
        // evaluation grants no write attributes although the kernel did (a share whose DACL names
        // the server's own accounts); or the folder could not be asked about FILE_DELETE_CHILD.
        // A local volume reaches none of them, so the verdict is driven directly and the resolver
        // through its storeRights seam. Killing mutations: trust the token evaluation everywhere
        // (drop the agreement check; the disagreeing leg refuses the store); refuse a store whose
        // DACL could not be evaluated; refuse on any folder error; drop one report; drop the
        // degraded callback on its way down from ResolveStoragePath (the resolver leg hears nothing).
        const string store = @"C:\portable\presets.json";
        const int writeData = 0x2, appendData = 0x4, writeAttributes = 0x100, delete = 0x10000, readControl = 0x20000;
        const int saveRights = writeAttributes | writeData | appendData;
        var legs = new (string Name, int? Rights, int FolderError, bool Kept, bool FolderAsked, string? Report)[]
        {
            ("a DACL that could not be evaluated", null, 0, true, false, "could not be read or evaluated"),
            ("an evaluation granting neither write attributes nor data although the kernel granted write attributes", readControl, 0, true, false, "no write attributes although the kernel did"),
            ("no DELETE and a folder that could not be asked", readControl | saveRights, 87, true, true, "could not be asked (win32 87)"),
            ("every right Save needs", readControl | saveRights | delete, 0, true, false, null),
            ("no DELETE and a folder that grants deleting children", readControl | saveRights, 0, true, true, null),
            ("no DELETE and a folder that refuses deleting children", readControl | saveRights, 5, false, true, null),
            ("write attributes without the data rights", readControl | writeAttributes, 0, false, false, null),
        };
        foreach (var leg in legs)
        {
            var reports = new List<string>();
            var asked = false;
            var kept = StorageService.SavingAllowed(store, leg.Rights, () => { asked = true; return leg.FolderError; }, reports.Add);
            Check.True(kept == leg.Kept, $"a store with {leg.Name} must be {(leg.Kept ? "kept" : "refused")}; it was {(kept ? "kept" : "refused")}");
            Check.True(asked == leg.FolderAsked, $"a store with {leg.Name} {(leg.FolderAsked ? "asks" : "does not ask")} about its folder");
            Check.True(leg.Report is null
                    ? reports.Count == 0
                    : reports.Count == 1 && reports[0].Contains(store, StringComparison.Ordinal) && reports[0].Contains(leg.Report, StringComparison.Ordinal),
                $"a store with {leg.Name} {(leg.Report is null ? "reports nothing" : $"is reported once, naming the store and '{leg.Report}'")}; got [{string.Join(" | ", reports)}]");
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("NOTE  resolver leg of the unevaluable-ACL case UNCHECKED: not Windows");
            return;
        }

        var root = NewResolverRoot();
        try
        {
            var portable = Path.Combine(root, "portable", "presets.json");
            var appData = Path.Combine(root, "appdata", "presets.json");
            new StorageService(portable).Save(StateWith("live"));
            var warnings = new List<string>();
            var degraded = new List<string>();
            int? evaluated = null;
            var resolved = StorageService.ResolveStoragePath(portable, appData, warn: warnings.Add, degraded: degraded.Add,
                storeRights: rights => { evaluated = rights; return null; });
            Check.True(evaluated is int granted && (granted & (saveRights | delete)) == (saveRights | delete),
                $"the fixture store's own evaluation must run and grant every right Save needs; got {(evaluated is int g ? $"0x{g:X}" : "none")}");
            Check.True(resolved == (portable, true) && warnings.Count == 0,
                $"a store whose DACL could not be evaluated keeps the portable store without a fallback warning; got '{resolved.Path}' with {warnings.Count} warning(s)");
            Check.True(degraded.Count == 1 && degraded[0].Contains(portable, StringComparison.Ordinal) && degraded[0].Contains("could not be read or evaluated", StringComparison.Ordinal),
                $"and the resolution reports the degraded check once, naming the store; got [{string.Join(" | ", degraded)}]");

            degraded.Clear();
            Check.True(StorageService.ResolveStoragePath(portable, appData, degraded: degraded.Add) == (portable, true) && degraded.Count == 0,
                $"the same store evaluated in full reports nothing; got [{string.Join(" | ", degraded)}]");
            Check.False(Directory.Exists(Path.GetDirectoryName(appData)), "the AppData store is never touched");
        }
        finally
        {
            DeleteResolverRoot(root);
        }
    }

    // Adds or removes one explicit rule for the current user on a file or folder.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetCurrentUserRule(string path, System.Security.AccessControl.FileSystemRights rights, System.Security.AccessControl.AccessControlType type, bool add)
    {
        var rule = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, rights, type);
        var folder = new DirectoryInfo(path);
        var acl = folder.GetAccessControl();
        if (add) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
        folder.SetAccessControl(acl);
    }

    // Removes every explicit rule for the current user from a fixture folder and the files in
    // it. Save's File.Replace carries the store's explicit rules onto the new store, so a leg
    // can leave them on more than one file.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ClearCurrentUserRules(string folder)
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var folderInfo = new DirectoryInfo(folder);
        var folderAcl = folderInfo.GetAccessControl();
        folderAcl.PurgeAccessRules(user);
        folderInfo.SetAccessControl(folderAcl);
        foreach (var path in Directory.GetFiles(folder))
        {
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            acl.PurgeAccessRules(user);
            file.SetAccessControl(acl);
        }
    }
}
