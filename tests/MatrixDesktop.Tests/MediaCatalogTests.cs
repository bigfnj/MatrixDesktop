using MatrixDesktop.Shared;

namespace MatrixDesktop.Tests;

internal static class MediaCatalogTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("An unconfigured scene grants no local media", Empty),
        ("The media catalog grants only supported top-level images", SupportedImages),
        ("Personal and donor film groups remain distinguishable", Groups),
        ("Missing picture folders report an actionable warning", Missing),
        ("Empty and oversized media are excluded before any read", SizeLimits),
        ("The picture catalog bounds the number of granted images", CountLimit),
        ("Music grants only an existing supported audio file", Audio),
        ("Removed files revoke their previously granted media resource", Removed),
        ("UC3D arguments preserve media paths and boolean intent", CommandRoundTrip),
        ("Donor manifests preserve captions and overlapping movie membership", Manifest),
        ("A malformed donor manifest does not retain partial grants", BadManifest),
        ("New presets are introduced once without restoring deleted old presets", NewPreset),
    ];

    private static void WithFolder(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "MatrixDesktop-MediaTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Empty()
    {
        var warnings = new List<string>();
        Check.Equal(0, MediaCatalog.Images(null, "", warnings).Count, "unconfigured media must be empty");
        Check.Equal(0, warnings.Count, "unused media needs no warning");
    }

    private static void SupportedImages() => WithFolder(root =>
    {
        foreach (var name in new[] { "A portrait.jpg", "B image.PNG", "C frame.BMP", "notes.txt" })
            File.WriteAllBytes(Path.Combine(root, name), [1, 2]);
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllBytes(Path.Combine(root, "nested", "hidden.jpg"), [3]);
        var entries = MediaCatalog.Images(root, null, new List<string>());
        Check.Equal(3, entries.Count, "unsupported files and nested folders must not be granted");
        Check.Equal("A portrait", entries[0].Caption, "caption strips only the extension");
        Check.Equal("image/png", entries[1].ContentType, "MIME matching is case insensitive");
        Check.Equal("custom", entries[0].Group, "personal images retain their group");
    });

    private static void Groups() => WithFolder(root =>
    {
        var personal = Path.Combine(root, "My pictures");
        var donor = Path.Combine(root, "Donor");
        Directory.CreateDirectory(personal);
        Directory.CreateDirectory(donor);
        File.WriteAllBytes(Path.Combine(personal, "Photo.jpg"), [1]);
        foreach (var group in new[] { "matrix", "reloaded", "revolutions" })
        {
            Directory.CreateDirectory(Path.Combine(donor, group));
            File.WriteAllBytes(Path.Combine(donor, group, "Neo.jpg"), [2]);
        }
        var entries = MediaCatalog.Images(personal, donor, new List<string>());
        Check.Equal(4, entries.Count, "each explicit source group is included");
        Check.Equal(4, entries.Select(e => e.Group).Distinct().Count(), "film and personal groups are not flattened");
        Check.Equal(4, entries.Select(e => e.Id).Distinct().Count(), "resource ids are unique across roots");
    });

    private static void Missing() => WithFolder(root =>
    {
        var warnings = new List<string>();
        Check.Equal(0, MediaCatalog.Images(Path.Combine(root, "missing"), null, warnings).Count, "missing root has no grants");
        Check.Equal(1, warnings.Count, "an explicitly selected missing folder must be visible to the user");
    });

    private static void SizeLimits() => WithFolder(root =>
    {
        File.WriteAllBytes(Path.Combine(root, "empty.jpg"), []);
        using (var file = File.Create(Path.Combine(root, "large.jpg"))) file.SetLength(MediaCatalog.MaxFileBytes + 1);
        File.WriteAllBytes(Path.Combine(root, "small.jpg"), [1]);
        var entries = MediaCatalog.Images(root, null, new List<string>());
        Check.Equal(1, entries.Count, "zero-byte and oversized files must be rejected");
        Check.Equal("small", entries[0].Caption, "only valid-size media is granted");
    });

    private static void CountLimit() => WithFolder(root =>
    {
        for (var i = 0; i < MediaCatalog.MaxImages + 1; i++) File.WriteAllBytes(Path.Combine(root, $"{i:D4}.png"), [1]);
        var warnings = new List<string>();
        Check.Equal(MediaCatalog.MaxImages, MediaCatalog.Images(root, null, warnings).Count, "catalog size must be bounded");
        Check.True(warnings.Any(w => w.Contains("limited", StringComparison.OrdinalIgnoreCase)), "limits must report their effect");
    });

    private static void Audio() => WithFolder(root =>
    {
        var path = Path.Combine(root, "My song.MP3");
        File.WriteAllBytes(path, [1]);
        var warnings = new List<string>();
        Check.Equal("audio/mpeg", MediaCatalog.Audio(path, warnings)!.ContentType, "audio MIME must be correct");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "test data");
        Check.True(MediaCatalog.Audio(Path.Combine(root, "notes.txt"), warnings) is null, "arbitrary files must not be served as music");
        Check.True(warnings.Count > 0, "an invalid audio selection needs a warning");
    });

    private static void Removed() => WithFolder(root =>
    {
        var path = Path.Combine(root, "Photo.jpg");
        File.WriteAllBytes(path, [1]);
        var entry = MediaCatalog.Images(root, null, new List<string>())[0];
        Check.True(MediaCatalog.StillAllowed(entry), "existing grant must be readable");
        File.Delete(path);
        Check.True(!MediaCatalog.StillAllowed(entry), "a stale grant cannot remain readable");
    });

    private static void CommandRoundTrip()
    {
        var metadata = MatrixDesktopConfigurator.ArgumentCatalog.Create();
        var builder = new MatrixDesktopConfigurator.CommandBuilder(metadata);
        var importer = new MatrixDesktopConfigurator.ArgumentImporter(metadata);
        var imported = importer.Import("--scene uc3d --showPictures true --pictureDirectory \"D:\\My Pictures\\Neo\" --pictureDepth 0.7 --showClock true", builder.CreateDefaultDraft());
        var args = builder.BuildArguments(imported.Draft, false, true);
        var query = MatrixDesktop.MatrixArgs.BuildQueryString(args);
        Check.True(query.Contains("scene=uc3d"), "scene reaches the renderer");
        Check.True(query.Contains("showPictures=true"), "picture intent reaches the host and renderer");
        Check.True(Uri.UnescapeDataString(query).Contains(@"D:\My Pictures\Neo"), "path survives spaces and backslashes");
        Check.True(MatrixDesktop.MatrixArgs.BuildQueryString(["--showClock"]).Contains("showClock=true"), "a bare new boolean is accepted");
    }

    private static void Manifest() => WithFolder(root =>
    {
        File.WriteAllBytes(Path.Combine(root, "neo.jpg"), [1]);
        File.WriteAllText(Path.Combine(root, "uc3d-media.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = 1,
            images = new[] { new { file = "neo.jpg", caption = "Neo & Trinity", groups = new[] { "reloaded", "revolutions" } } },
        }));
        var images = MediaCatalog.Images(null, root, new List<string>());
        Check.Equal(1, images.Count, "manifest and raw folder must not duplicate the same image");
        Check.Equal("Neo & Trinity", images[0].Caption, "the donor caption is preserved");
        Check.Equal(2, images[0].Groups!.Length, "overlapping original film membership is retained");
    });

    private static void BadManifest() => WithFolder(root =>
    {
        File.WriteAllBytes(Path.Combine(root, "neo.jpg"), [1]);
        File.WriteAllText(Path.Combine(root, "uc3d-media.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = 1,
            images = new[]
            {
                new { file = "neo.jpg", caption = "Neo", groups = new[] { "reloaded" } },
                new { file = "../outside.jpg", caption = "Outside", groups = new[] { "reloaded" } },
            },
        }));
        var warnings = new List<string>();
        var images = MediaCatalog.Images(null, root, warnings);
        Check.Equal(1, images.Count, "invalid manifests must roll back their partial grants before safe folder fallback");
        Check.True(warnings.Count > 0, "invalid manifest needs a visible diagnostic");
        foreach (var schema in new[] { "1.5", "2147483648", "\"1\"", "null" })
        {
            File.WriteAllText(Path.Combine(root, "uc3d-media.json"), "{\"schema\":" + schema + ",\"images\":[]}");
            warnings.Clear();
            Check.Equal(1, MediaCatalog.Images(null, root, warnings).Count, "invalid schema must fall back to the local folder: " + schema);
            Check.True(warnings.Count > 0, "invalid schema needs a diagnostic: " + schema);
        }
    });

    private static void NewPreset()
    {
        var state = new MatrixDesktopConfigurator.ConfiguratorState { PresetSeedVersion = 2 };
        var builder = new MatrixDesktopConfigurator.CommandBuilder(MatrixDesktopConfigurator.ArgumentCatalog.Create());
        var importer = new MatrixDesktopConfigurator.ArgumentImporter(MatrixDesktopConfigurator.ArgumentCatalog.Create());
        Check.True(MatrixDesktopConfigurator.PresetSeeder.Seed(state, builder, importer), "new seed version must apply");
        Check.SetEqual(new[] { "seed-uc3d-trilogy", "seed-zmatrix-desktop" }, state.UserPresets.Select(preset => preset.Id), "only starters introduced after version2 must appear; deleted original starters must not return");
        state.UserPresets.Clear();
        Check.True(!MatrixDesktopConfigurator.PresetSeeder.Seed(state, builder, importer), "seed version must advance");
        Check.Equal(0, state.UserPresets.Count, "a deleted UC3D starter must remain deleted");
    }
}
