using System.Text.Json;
using System.Text.Json.Nodes;
using MatrixDesktopConfigurator;

namespace MatrixDesktop.Tests;

internal static class CommandBuilderTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("A default draft generates the bare executable name", DefaultDraftIsBare),
        ("Only values that differ from the default are emitted", DefaultsAreSkipped),
        ("Stripe colours are omitted for a non stripe effect", StripeColorsGatedOff),
        ("Stripe colours are emitted for a stripe effect", StripeColorsGatedOn),
        ("Camera is omitted unless the effect is mirror", CameraRequiresMirror),
        ("Window mode emits the matching wrapper flag", WindowModeFlags),
        ("A monitor index is dropped unless single monitor is selected", MonitorRequiresSingleMonitor),
        ("A test launch forces windowed and disables any-key exit", TestLaunchIsSafe),
        ("A test launch does not cover other windows or hide the cursor", TestLaunchIsInspectable),
        ("Shell significant characters are quoted", ShellQuoting),
        ("An empty value is quoted rather than dropped from the token list", EmptyValueQuoted),
        ("Generated arguments round trip through the importer", RoundTrip),
        ("The preview query string skips defaults so effect palettes survive", PreviewQuerySkipsDefaults),
        ("The preview query string percent encodes its values", PreviewQueryEncodes),
        ("UC3D scene defaults generate a compact command and round trip", Uc3dSceneDefaultsRoundTrip),
        ("UC3D overrides equal to Matrix defaults survive commands and previews", Uc3dMatrixDefaultOverrides),
        ("The untouched UC3D starter migrates to the current profile once", Uc3dStarterMigrates),
        ("UC3D starter migration preserves edited renamed and deleted presets", Uc3dStarterEditsSurvive),
        ("UC3D original slider ranges retain endpoints and clamp finite integers", Uc3dSliderRanges),
        ("UC3D explicit control mode and native options take precedence in either order", Uc3dControlInference),
        ("UC3D commands omit inactive units while preserving saved values", Uc3dInactiveUnits),
        ("The untouched version 4 UC3D starter migrates without changing custom drafts", Uc3dVersion4Migration),
        ("UC3D randomization edits active controls and retains the alternate unit set", Uc3dRandomization),
        ("Scene placements name real fields groups and anchors and UC3D shows its colours together", ScenePlacementsResolve),
        ("Untouched editors show what the renderer draws for each version and effect", UntouchedEditorsShowTheRender),
        ("A command that names the palette effect keeps its effect cursor through import and Copy", NamedPaletteKeepsItsCursor),
        ("Use for screensaver saves exactly what the preview renders", ScreensaverMatchesPreview),
        ("Stored drafts keep their render when version and effect defaults arrive", StoredDraftsKeepTheirRender),
        ("A base value chosen after the migration survives save and reload", FreshBaseValueSurvivesReload),
    ];

    // Values read from web/js/config.js and regl/stripePass.js, not from the catalog, so a wrong
    // C# table cannot agree with itself here. White is hsl(0, 0, 1); operator's cursor is
    // hsl(0.375, 1, 0.66) at config.js:201, neomatrixology's hsl(0.167, 1, 0.75) at :197.
    private static readonly Dictionary<string, (string Cursor, double Intensity, double Columns)> RenderedByVersion = new()
    {
        ["classic"] = ("{\"r\":0.756,\"g\":1,\"b\":0.46}", 2, 80),
        ["operator"] = ("{\"r\":0.32,\"g\":1,\"b\":0.49}", 3, 108),
        ["neomatrixology"] = ("{\"r\":0.999,\"g\":1,\"b\":0.5}", 2, 40),
    };

    private static string Rows(int repeat, params string[] colors)
        => "[" + string.Join(",", colors.SelectMany(color => Enumerable.Repeat(color, repeat))) + "]";

    private static readonly string TransPrideRows = Rows(3, "{\"r\":0.36,\"g\":0.81,\"b\":0.98}", "{\"r\":0.96,\"g\":0.66,\"b\":0.72}",
        "{\"r\":1,\"g\":1,\"b\":1}", "{\"r\":0.96,\"g\":0.66,\"b\":0.72}", "{\"r\":0.36,\"g\":0.81,\"b\":0.98}");

    private static readonly string PrideRows = Rows(2, "{\"r\":0.89,\"g\":0.01,\"b\":0.01}", "{\"r\":1,\"g\":0.55,\"b\":0}",
        "{\"r\":1,\"g\":0.93,\"b\":0}", "{\"r\":0,\"g\":0.5,\"b\":0.15}", "{\"r\":0,\"g\":0.3,\"b\":1}", "{\"r\":0.46,\"g\":0.03,\"b\":0.53}");

    private static string Json(JsonNode? node) => node?.ToJsonString() ?? "null";

    // config.js:652-685 and stripePass.js:39: an imported command that names only a version and an
    // effect must leave every editor on what that command renders, and the regenerated command
    // must leave those fields out so the renderer draws exactly that. A command that names the
    // palette effect is NamedPaletteKeepsItsCursor's, since CommandBuilder never names palette.
    private static void UntouchedEditorsShowTheRender()
    {
        var failures = new List<string>();
        foreach (var (version, rendered) in RenderedByVersion)
        foreach (var effect in new[] { null, "plain", "stripes", "customStripes", "pride", "trans", "transPride" })
        {
            var draft = Importer.Import($"--version {version}" + (effect is null ? "" : $" --effect {effect}"), Draft()).Draft;
            var named = effect is not null;
            var expected = new Dictionary<string, string>
            {
                ["cursorColor"] = named ? "{\"r\":1,\"g\":1,\"b\":1}" : rendered.Cursor,
                ["cursorIntensity"] = (named ? 2 : rendered.Intensity).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["numColumns"] = rendered.Columns.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (ArgumentImporter.IsStripeEffect(effect)) expected["stripeColors"] = effect == "pride" ? PrideRows : TransPrideRows;
            var query = Builder.BuildWebQueryString(draft);
            foreach (var (field, value) in expected)
            {
                if (Json(draft[field]) != value) failures.Add($"{version}/{effect ?? "no effect"} {field} editor shows {Json(draft[field])}, renderer draws {value}");
                if (query.Contains(field + "=", StringComparison.Ordinal)) failures.Add($"{version}/{effect ?? "no effect"} sends {field} although it is untouched: {query}");
            }
        }
        Check.True(failures.Count == 0, string.Join("; ", failures));
    }

    // R2-11. config.js:654-670 applies an effect's implied values whenever a command names one,
    // palette included: a white cursor (UC3D keeps its own), cursor intensity 2, a white glint
    // and glyph intensity 1. CommandBuilder never names the default palette effect, so an
    // imported --effect palette keeps those values in its editors and sends each one the version
    // draws differently, or Copy, Test, the preview and the screensaver would change the render.
    // Applied still lists only what the command named. Killing mutations: drop the importer's
    // named-palette branch, or let AdoptRenderedDefaults see only the applied keys.
    private static void NamedPaletteKeepsItsCursor()
    {
        const string white = "{\"r\":1,\"g\":1,\"b\":1}";
        var uc3dCursor = Json(JsonSerializer.SerializeToNode(Metadata.SceneDefaults["uc3d"]["cursorColor"], new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var failures = new List<string>();
        foreach (var (command, cursor, applied, sent) in new[]
        {
            ("--effect palette", white, "effect", "cursorColor=1,1,1"),
            ("--version operator --effect palette", white, "effect version", "cursorColor=1,1,1 cursorIntensity=2 version=operator"),
            ("--version trinity --effect palette", white, "effect version", "cursorColor=1,1,1 glintColor=1,1,1 version=trinity"),
            ("--scene uc3d --version operator --effect palette", uc3dCursor, "effect scene version", "cursorIntensity=2 scene=uc3d version=operator"),
            ("--effect palette --cursorColor 1,0,0 --version operator", "{\"r\":1,\"g\":0,\"b\":0}", "cursorColor effect version", "cursorColor=1,0,0 cursorIntensity=2 version=operator"),
        })
        {
            var imported = Importer.Import(command, Draft());
            foreach (var (field, value) in new[] { ("cursorColor", cursor), ("cursorIntensity", "2"), ("glintColor", white), ("glyphIntensity", "1") })
                if (Json(imported.Draft[field]) != value) failures.Add($"'{command}' {field} editor shows {Json(imported.Draft[field])}, renderer draws {value}");
            var query = string.Join(" ", QueryPairs(Builder.BuildWebQueryString(imported.Draft)).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
            if (query != sent) failures.Add($"'{command}' previews '{query}', not '{sent}'");
            if (string.Join(" ", imported.Applied) != applied) failures.Add($"'{command}' reports applying '{string.Join(" ", imported.Applied)}', not '{applied}'");
        }
        // The guide's own example (MatrixDesktop_Argument_Guide.txt, the palette effect).
        var guide = Importer.Import("MatrixDesktop.exe --windowed --effect palette --clickRipples", Draft()).Draft;
        var copied = Cmd(guide);
        if (copied != "MatrixDesktop.exe --windowed --clickRipples true --cursorColor 1,1,1") failures.Add($"the guide's palette example copies as '{copied}'");
        Check.True(failures.Count == 0, string.Join("; ", failures));
    }

    private static Dictionary<string, string> WebPairs(IEnumerable<string> arguments)
    {
        var webNames = Metadata.Groups.SelectMany(group => group.Fields).Where(field => field.Scope == "web").Select(field => field.ArgName).ToHashSet(StringComparer.Ordinal);
        var list = arguments.ToList();
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count - 1; i++)
            if (list[i].StartsWith("--", StringComparison.Ordinal) && webNames.Contains(list[i][2..])) pairs[list[i][2..]] = list[++i];
        return pairs;
    }

    private static Dictionary<string, string> QueryPairs(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);

    // ConfiguratorForm.UseScreensaver saves BuildScreensaverArguments. It used to emit every
    // default, which drew the catalog's stripe rows on pride and classic values on paradise.
    private static void ScreensaverMatchesPreview()
    {
        foreach (var command in new[] { "--effect pride", "--version paradise --effect stripes", "--version operator", "--scene uc3d --version operator --effect plain", "--numColumns 90 --cursorColor 1,0,0" })
        {
            var draft = Importer.Import(command, Draft()).Draft;
            var saver = WebPairs(Builder.BuildScreensaverArguments(draft));
            var preview = QueryPairs(Builder.BuildWebQueryString(draft));
            Check.Equal(string.Join(" ", preview.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)),
                string.Join(" ", saver.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)),
                $"the screensaver profile for '{command}' must carry exactly the preview's settings");
        }
    }

    // What the renderer draws for each active web field: the emitted value, or what config.js
    // draws without it (EffectiveDefault, which tests/effective-defaults-tests.mjs holds to the
    // real config.js and the golden values above hold to the source).
    private static Dictionary<string, string> Rendered(JsonObject draft, Func<ArgumentDefinition, bool> emitted)
    {
        var scene = draft["scene"]?.GetValue<string>() ?? "matrix";
        var mode = Uc3dControlProfile.Mode(draft);
        var version = draft["version"]?.GetValue<string>() ?? "classic";
        var effect = draft["effect"]?.GetValue<string>() ?? "palette";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Metadata.Groups.SelectMany(group => group.Fields).Where(field => field.Scope == "web"))
        {
            if (!Uc3dControlProfile.IsActive(field.Id, draft) || !ZMatrixControlProfile.IsActive(field.Id, draft) || CommandBuilder.IsInactiveForEffect(field.Id, effect) || field.Id == "uc3dControls") continue;
            result[field.Id] = emitted(field) ? Json(draft[field.Id]) : Json(JsonSerializer.SerializeToNode(Metadata.EffectiveDefault(field, scene, mode, version, effect), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        return result;
    }

    private static JsonObject StoredDraft(params (string Key, JsonNode? Value)[] values) => StoredDraft(Draft(), values);

    private static JsonObject StoredDraft(JsonObject draft, params (string Key, JsonNode? Value)[] values)
    {
        foreach (var (key, value) in values) draft[key] = value;
        return draft;
    }

    // Every seeded starter as builds before AUD-13 stored it: the command's own values, and each
    // field it does not name on the scene or catalog value the old importer left there. Read
    // from PresetSeeder itself so a new starter joins this replay.
    private static IEnumerable<JsonObject> StartersAsStoredBefore()
    {
        var starters = (System.Collections.IEnumerable)typeof(PresetSeeder)
            .GetField("StarterPresets", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        foreach (var starter in starters)
        {
            var imported = Importer.Import((string)starter.GetType().GetProperty("Command")!.GetValue(starter)!, Draft());
            var draft = imported.Draft;
            var scene = draft["scene"]!.GetValue<string>();
            var mode = Uc3dControlProfile.Mode(draft);
            foreach (var field in Metadata.Groups.SelectMany(group => group.Fields).Where(field => field.Scope == "web" && field.Id is not ("scene" or "uc3dControls")))
                if (draft[field.Id] is not null && !imported.Applied.Contains(field.Id, StringComparer.OrdinalIgnoreCase))
                    draft[field.Id] = JsonSerializer.SerializeToNode(Metadata.LegacyDefault(field, scene, mode), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            yield return draft;
        }
    }

    // Drafts as builds before AUD-13 saved them: fully populated, with untouched fields on the
    // catalog or scene value that the old builder left out, and the seeded starters. The
    // migration in PresetSeeder must keep every one rendering exactly what it rendered then,
    // Test and preview alike.
    private static void StoredDraftsKeepTheirRender()
    {
        var drafts = new[]
        {
            StoredDraft(("version", "operator")),
            StoredDraft(("version", "operator"), ("numColumns", 108.0)),
            StoredDraft(("version", "neomatrixology"), ("effect", "plain")),
            StoredDraft(("effect", "pride")),
            StoredDraft(("effect", "stripes"), ("version", "paradise"), ("font", "resurrections"), ("numColumns", 150.0)),
            StoredDraft(("effect", "customStripes"), ("stripeColors", JsonNode.Parse("[{\"r\":1,\"g\":0,\"b\":0},{\"r\":0,\"g\":0,\"b\":1}]"))),
            StoredDraft(("version", "trinity"), ("effect", "mirror"), ("camera", true)),
            StoredDraft(Builder.CreateSceneDefaultDraft("uc3d"), ("version", "operator")),
            StoredDraft(Builder.CreateSceneDefaultDraft("uc3d"), ("uc3dControls", "legacy"), ("version", "paradise"), ("effect", "plain")),
            StoredDraft(("scene", "zmatrix"), ("version", "operator"), ("effect", "pride")),
        }.Concat(StartersAsStoredBefore()).ToArray();
        const int starterStart = 10;
        Check.Equal(starterStart + 5, drafts.Length, "the replay must include the five seeded starters");
        var state = new ConfiguratorState { SchemaVersion = 1, PresetSeedVersion = 6, LastDraft = drafts[0].DeepClone().AsObject() };
        foreach (var draft in drafts) state.UserPresets.Add(new UserPreset { Values = draft.DeepClone().AsObject() });
        Check.True(PresetSeeder.Seed(state, Builder, Importer), "the first load of an old store must report the migration so the host saves it");
        Check.Equal(PresetSeeder.EffectiveDefaultsSchemaVersion, state.SchemaVersion, "the migration must record that it ran");
        for (var i = 0; i < drafts.Length; i++)
        {
            var old = drafts[i];
            var migrated = state.UserPresets[i].Values;
            var before = Rendered(old, field => Json(old[field.Id]) != Json(JsonSerializer.SerializeToNode(Metadata.LegacyDefault(field, old["scene"]!.GetValue<string>(), Uc3dControlProfile.Mode(old)), new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            var query = QueryPairs(Builder.BuildWebQueryString(migrated));
            var after = Rendered(migrated, field => query.ContainsKey(field.ArgName));
            foreach (var (field, value) in before)
            {
                Check.Equal(value, after[field], $"stored draft {i} ({old["scene"]}/{old["version"]}/{old["effect"]}) {field} must render as before the migration");
                Check.Equal(value, Json(migrated[field]), $"stored draft {i} ({old["scene"]}/{old["version"]}/{old["effect"]}) {field} editor must show what renders");
            }
        }
        Check.True(JsonNode.DeepEquals(state.LastDraft, state.UserPresets[0].Values), "the last draft migrates like the presets");
        Check.True(Enumerable.Range(starterStart, 5).Any(i => !JsonNode.DeepEquals(drafts[i], state.UserPresets[i].Values)),
            "the migration must move at least one stored starter (paradise draws its own speeds and stripe rows), or the starter replay proves nothing");
    }

    // Codex C01: the migration runs once. A base colour chosen on an effect that draws
    // something else must be sent, survive a save and a reload, and render as chosen;
    // re-normalising on every load would take it away again.
    private static void FreshBaseValueSurvivesReload()
    {
        var draft = Importer.Import("--effect plain", Draft()).Draft;
        draft["cursorColor"] = JsonNode.Parse("{\"r\":0.756,\"g\":1,\"b\":0.46}");
        var state = new ConfiguratorState { SchemaVersion = PresetSeeder.EffectiveDefaultsSchemaVersion, PresetSeedVersion = 6, LastDraft = draft };
        state.UserPresets.Add(new UserPreset { Values = draft.DeepClone().AsObject() });
        var reloaded = JsonSerializer.Deserialize<ConfiguratorState>(JsonSerializer.Serialize(state))!;
        PresetSeeder.Seed(reloaded, Builder, Importer);
        foreach (var values in new[] { reloaded.LastDraft, reloaded.UserPresets[0].Values })
        {
            var query = QueryPairs(Builder.BuildWebQueryString(values));
            Check.Equal("0.756,1,0.46", query.GetValueOrDefault("cursorColor") ?? "(left out)", "the base cursor colour chosen on plain must be sent after a reload");
        }
    }

    private static readonly ConfiguratorMetadata Metadata = ArgumentCatalog.Create();
    private static readonly CommandBuilder Builder = new(Metadata);
    private static readonly ArgumentImporter Importer = new(Metadata);

    private static JsonObject Draft() => Builder.CreateDefaultDraft();
    private static string Cmd(JsonObject d, bool forTest = false) => Builder.BuildCommand(d, false, forTest);

    private static void DefaultDraftIsBare()
        => Check.Equal("MatrixDesktop.exe", Cmd(Draft()),
            "an untouched draft must generate no flags, or every copied command would carry noise");

    private static void DefaultsAreSkipped()
    {
        var d = Draft();
        d["numColumns"] = 120.0;
        var cmd = Cmd(d);
        Check.Contains(cmd, "--numColumns 120", "a changed value must appear");
        Check.DoesNotContain(cmd, "--density", "an unchanged value must not appear");
    }

    private static void StripeColorsGatedOff()
    {
        var d = Draft();
        d["effect"] = "palette";
        d["stripeColors"] = new JsonArray(new JsonObject { ["r"] = 1.0, ["g"] = 0.0, ["b"] = 0.0 });
        Check.DoesNotContain(Cmd(d), "--stripeColors",
            "the palette effect cannot use stripe colours, and emitting them would be misleading");
    }

    private static void StripeColorsGatedOn()
    {
        var d = Draft();
        d["effect"] = "stripes";
        d["stripeColors"] = new JsonArray(new JsonObject { ["r"] = 1.0, ["g"] = 0.0, ["b"] = 0.0 });
        Check.Contains(Cmd(d), "--stripeColors 1,0,0", "a stripe effect must emit its colours");
    }

    // AUD-03. The page greys Camera unless Effect is Mirror and drops edits to a greyed field, so
    // a camera=true set under Mirror cannot be cleared from another effect, while both renderers
    // open the webcam whenever camera is true. Killing mutation: delete the camera arm of
    // CommandBuilder.IsInactiveForEffect; the copied-command assertion fails first.
    private static void CameraRequiresMirror()
    {
        var effects = Metadata.Groups.SelectMany(group => group.Fields).Single(field => field.Id == "effect").Options.Select(option => option.Value).ToArray();
        Check.True(effects.Contains("mirror") && effects.Length > 2, "the catalog must offer Mirror and other effects");
        foreach (var effect in effects.Where(effect => effect != "mirror"))
        {
            var d = Draft();
            d["effect"] = effect;
            d["camera"] = true;
            var saved = d.DeepClone();
            Check.DoesNotContain(Cmd(d), "--camera", $"the copied command must not open the webcam for effect {effect}");
            Check.DoesNotContain(Builder.BuildWebQueryString(d), "camera=", $"the preview must not open the webcam for effect {effect}");
            Check.False(Builder.BuildArguments(d, true, false).Contains("--camera"), $"the screensaver profile must not open the webcam for effect {effect}");
            Check.False(Builder.BuildArguments(d, false, true).Contains("--camera"), $"Test must not open the webcam for effect {effect}");
            Check.True(JsonNode.DeepEquals(saved, d), "building must leave the saved camera value for a return to Mirror");
        }
        var mirror = Draft();
        mirror["effect"] = "mirror";
        mirror["camera"] = true;
        Check.Contains(Cmd(mirror), "--effect mirror --camera true", "Mirror still asks for the webcam");
        Check.Contains(Builder.BuildWebQueryString(mirror), "camera=true", "the Mirror preview still asks for the webcam");
        Check.True(Builder.BuildArguments(mirror, false, true).Contains("--camera"), "a Mirror test still asks for the webcam");
        mirror["camera"] = false;
        Check.Contains(string.Join(" ", Builder.BuildArguments(mirror, true, false)), "--camera false", "Mirror with every default still names the camera setting");
    }

    private static void WindowModeFlags()
    {
        var windowed = Draft();
        windowed["windowMode"] = "windowed";
        Check.Contains(Cmd(windowed), "--windowed", "windowed mode has a documented flag");

        var single = Draft();
        single["windowMode"] = "single-monitor";
        Check.Contains(Cmd(single), "--single-monitor", "single monitor mode has a documented flag");
    }

    private static void MonitorRequiresSingleMonitor()
    {
        var spanning = Draft();
        spanning["monitor"] = 1.0;
        Check.DoesNotContain(Cmd(spanning), "--monitor",
            "spanning all monitors ignores an index, so emitting it would imply behaviour the app does not have");

        var single = Draft();
        single["windowMode"] = "single-monitor";
        single["monitor"] = 1.0;
        Check.Contains(Cmd(single), "--monitor 1", "with single monitor selected the index is meaningful");
    }

    private static void TestLaunchIsSafe()
    {
        var cmd = Cmd(Draft(), forTest: true);
        Check.Contains(cmd, "--windowed", "a test launch must be windowed so it can be inspected and closed");
        Check.Contains(cmd, "--no-exit-on-any-key",
            "without this, typing anywhere would close the test instance, since the app exits on any key by default");
    }

    private static void TestLaunchIsInspectable()
    {
        var d = Draft();
        d["topmost"] = true;
        d["hideCursor"] = true;
        var cmd = Cmd(d, forTest: true);
        Check.DoesNotContain(cmd, "--topmost", "a test window that covers everything is awkward to inspect");
        Check.DoesNotContain(cmd, "--hide-cursor", "a test window that hides the cursor is awkward to close");
    }

    private static void ShellQuoting()
    {
        var d = Draft();
        d["effect"] = "image";
        d["url"] = "http://example.com/a.png?x=1&y=2";
        var cmd = Cmd(d);
        Check.Contains(cmd, "\"http://example.com/a.png?x=1&y=2\"",
            "an unquoted ampersand would truncate the command in cmd.exe and split it in PowerShell");
    }

    private static void EmptyValueQuoted()
    {
        var d = Draft();
        d["effect"] = "image";
        d["url"] = "x";
        Check.Contains(Cmd(d), "--url x", "a simple value needs no quoting");
    }

    private static void RoundTrip()
    {
        var d = Draft();
        d["effect"] = "stripes";
        d["version"] = "3d";
        d["numColumns"] = 220.0;
        d["windowMode"] = "windowed";
        d["hideCursor"] = true;

        var reimported = Importer.Import(Builder.BuildCommand(d, false, false), Draft()).Draft;

        Check.Equal("stripes", (string)reimported["effect"]!, "a generated command must import back to the same effect");
        Check.Equal("3d", (string)reimported["version"]!, "and the same version");
        Check.Close(220, (double)reimported["numColumns"]!, 1e-9, "and the same column count");
        Check.Equal("windowed", (string)reimported["windowMode"]!, "and the same window mode");
        Check.True((bool)reimported["hideCursor"]!, "and the same cursor setting");
    }

    private static void PreviewQuerySkipsDefaults()
    {
        // stripePass.js uses an effect's built-in colours only when stripeColors is absent,
        // so emitting the default would flatten pride and trans into the same look.
        // Since AUD-13 a draft reaches pride holding pride's own rows: through the page's effect
        // switch, an import, or the one-time migration of a stored draft, modelled here.
        var d = Draft();
        d["effect"] = "pride";
        CommandBuilder.AdoptRenderedDefaults(Metadata, d);
        var query = Builder.BuildWebQueryString(d);
        Check.DoesNotContain(query, "stripeColors",
            "pride supplies its own colours, and sending the default set would override them");
    }

    private static void PreviewQueryEncodes()
    {
        var d = Draft();
        d["effect"] = "image";
        d["url"] = "a b&c";
        var query = Builder.BuildWebQueryString(d);
        Check.DoesNotContain(query, "a b&c", "a raw space and ampersand would corrupt the preview URL");
        Check.Contains(query, "url=a%20b%26c", "the value must be percent encoded");
    }

    private static void Uc3dSceneDefaultsRoundTrip()
    {
        var draft = Builder.CreateSceneDefaultDraft("uc3d");
        Check.Equal("MatrixDesktop.exe --scene uc3d", Cmd(draft), "the selected scene supplies its own defaults");
        Check.Equal("?scene=uc3d", Builder.BuildWebQueryString(draft), "preview and launch must use the same defaults");
        var imported = Importer.Import(Cmd(draft), Draft()).Draft;
        foreach (var key in Metadata.SceneDefaults["uc3d"].Keys)
            Check.True(JsonNode.DeepEquals(draft[key], imported[key]), $"UC3D default {key} must survive a command round trip");
        Check.Close(5, (double)imported["density"]!, 1e-9, "500 native streams at 100 columns needs density 5");
        Check.Close(1, (double)Draft()["density"]!, 1e-9, "the Matrix scene keeps its existing defaults");
    }

    private static void Uc3dMatrixDefaultOverrides()
    {
        var draft = Builder.CreateSceneDefaultDraft("uc3d");
        draft["uc3dControls"] = "legacy";
        draft["volumetric"] = false;
        draft["sidewaysMovement"] = false;
        draft["fallSpeed"] = 0.3;
        draft["numColumns"] = 80.0;
        var command = Cmd(draft);
        Check.Contains(command, "--volumetric false", "an explicit 2D override must survive the UC3D 3D default");
        Check.Contains(command, "--sidewaysMovement false", "an explicit sideways override must survive");
        Check.Contains(Builder.BuildWebQueryString(draft), "volumetric=false", "preview must preserve the same override");
        var imported = Importer.Import(command, Draft()).Draft;
        foreach (var key in new[] { "volumetric", "sidewaysMovement", "fallSpeed", "numColumns" })
            Check.True(JsonNode.DeepEquals(draft[key], imported[key]), $"UC3D override {key} must round trip");
    }

    private static UserPreset PreviousUc3dStarter()
    {
        var draft = Draft();
        RemoveOriginalControls(draft);
        draft["scene"] = "uc3d";
        draft["volumetric"] = true;
        draft["randomForwardMovement"] = true;
        draft["codeColor"] = new JsonObject { ["r"] = 0.0, ["g"] = 0.698, ["b"] = 0.0 };
        draft["cursorColor"] = new JsonObject { ["r"] = 0.0, ["g"] = 1.0, ["b"] = 0.698 };
        draft["pictureDelay"] = 60.0;
        draft["pictureDuration"] = 30.0;
        return new UserPreset { Id = "seed-uc3d-trilogy", Name = "UC3D Trilogy", Values = draft };
    }

    private static void Uc3dStarterMigrates()
    {
        var previous = PreviousUc3dStarter();
        var state = new ConfiguratorState
        {
            PresetSeedVersion = 3,
            UserPresets = [previous],
            SelectedPresetId = previous.Id,
            LastDraft = StorageService.CloneObject(previous.Values),
        };
        Check.True(PresetSeeder.Seed(state, Builder, Importer), "the current migration must run for the previous starter");
        Check.True(JsonNode.DeepEquals(Builder.CreateSceneDefaultDraft("uc3d"), previous.Values), "the exact old starter must use the current profile");
        Check.True(JsonNode.DeepEquals(previous.Values, state.LastDraft), "an untouched selected starter draft must migrate with its preset");
        Check.Equal(1, state.UserPresets.Count(preset => preset.Id == previous.Id), "migration updates the starter without duplicates");
        Check.True(!PresetSeeder.Seed(state, Builder, Importer), "the migration must run only once");
    }

    private static void Uc3dStarterEditsSurvive()
    {
        foreach (var edit in new[] { "value", "name", "deleted", "unsaved draft" })
        {
            var previous = PreviousUc3dStarter();
            if (edit == "value") previous.Values["numColumns"] = 123.0;
            if (edit == "name") previous.Name = "My UC3D";
            var savedValues = previous.Values.DeepClone();
            savedValues["uc3dControls"] = "legacy";
            var lastDraft = StorageService.CloneObject(previous.Values);
            lastDraft["numColumns"] = 111.0;
            var expectedDraft = StorageService.CloneObject(lastDraft);
            expectedDraft["uc3dControls"] = "legacy";
            var state = new ConfiguratorState
            {
                PresetSeedVersion = 3,
                UserPresets = edit == "deleted" ? [] : [previous],
                SelectedPresetId = previous.Id,
                LastDraft = StorageService.CloneObject(lastDraft),
            };
            Check.True(PresetSeeder.Seed(state, Builder, Importer), "the seed version advances even when no preset can change");
            if (edit == "deleted") Check.False(state.UserPresets.Any(preset => preset.Id == previous.Id), "a deleted starter must remain deleted");
            else if (edit != "unsaved draft") Check.True(JsonNode.DeepEquals(savedValues, previous.Values), "a user-owned starter must retain its values: " + edit);
            Check.True(JsonNode.DeepEquals(expectedDraft, state.LastDraft), "a custom unsaved draft must survive: " + edit);
            Check.Equal(edit == "name" ? "My UC3D" : "UC3D Trilogy", previous.Name, "preset names must survive migration");
        }
    }

    private static void RemoveOriginalControls(JsonObject draft)
    {
        foreach (var key in draft.Select(pair => pair.Key).Where(key => key.StartsWith("zm", StringComparison.Ordinal))
            .Concat(new[] { "processPriority", "wallpaperInput", "wallpaperPauseFullscreen", "wallpaperPauseBattery" }).ToArray()) draft.Remove(key);
        draft.Remove("uc3dControls");
        foreach (var key in Uc3dControlProfile.NativeKeys) draft.Remove(key);
    }

    // Replays configurator/js/app.js sceneSections over the real catalog. There, a placement
    // naming a missing field, group or anchor logs and leaves the editor in its catalog section,
    // which the owner would only see as the wrong layout. Here it fails by name.
    private static void ScenePlacementsResolve()
    {
        var catalogFields = Metadata.Groups.SelectMany(group => group.Fields).ToList();
        Check.True(Metadata.ScenePlacements.ContainsKey("uc3d"), "the UC3D colour placements must exist");
        foreach (var (scene, placements) in Metadata.ScenePlacements)
        {
            var layout = Metadata.Groups.ToDictionary(group => group.Id, group => group.Fields.Select(field => field.Id).ToList());
            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var placement in placements)
            {
                var where = $"{scene} placement of {placement.Field}";
                var matches = catalogFields.Where(field => field.Id == placement.Field).ToList();
                Check.Equal(1, matches.Count, where + " must name exactly one catalog field");
                Check.True(placed.Add(placement.Field), where + " must not be placed twice in one scene");
                Check.True(!string.IsNullOrWhiteSpace(placement.Label) && placement.Label != matches[0].Label,
                    where + " must give the field a different, non-empty name");
                if (placement.Group is null)
                {
                    Check.True(placement.After is null, where + " names an anchor but no group to move into");
                    continue;
                }
                Check.True(layout.ContainsKey(placement.Group), where + $" must move into an existing group, not '{placement.Group}'");
                layout.Single(pair => pair.Value.Contains(placement.Field)).Value.Remove(placement.Field);
                var target = layout[placement.Group];
                var anchor = target.IndexOf(placement.After ?? string.Empty);
                Check.True(anchor >= 0, where + $" must follow a field present in {placement.Group} by then, not '{placement.After}'");
                target.Insert(anchor + 1, placement.Field);
            }

            if (scene != "uc3d") continue;
            var uc3d = layout["uc3d"];
            var at = uc3d.IndexOf("codeColor");
            Check.True(at >= 0 && uc3d.Skip(at).Take(3).SequenceEqual(["codeColor", "cursorColor", "backgroundColor"]),
                "UC3D must show Normal, Highlight and Background colour together, as the original settings dialog does: " + string.Join(", ", uc3d));
            Check.False(layout["colors"].Contains("cursorColor") || layout["colors"].Contains("backgroundColor"),
                "a colour moved into UC3D must leave Colors, or the page would offer two editors for one key");
            var labels = placements.ToDictionary(placement => placement.Field, placement => placement.Label);
            Check.Contains(labels["cursorColor"], "Highlight", "the owner looks for the original dialog's Highlight Colour");
            Check.Contains(labels["backgroundColor"], "Background", "the owner looks for the original dialog's Background Colour");
        }
    }

    private static void Uc3dSliderRanges()
    {
        var ranges = new (string Key, double Min, double Max, double Default)[]
        {
            ("uc3dAmount", 100, 1500, 500), ("uc3dGlyphSize", 10, 40, 12),
            ("uc3dFallSpeed", 0, 150, 6), ("uc3dForwardSpeed", 0, 120, 6),
            ("uc3dPictureScale", 100, 300, 100), ("uc3dPictureDelay", 0, 120, 60),
            ("uc3dClockSize", 50, 170, 90),
        };
        foreach (var (key, min, max, expectedDefault) in ranges)
        {
            var field = Metadata.Groups.SelectMany(group => group.Fields).Single(field => field.Id == key);
            Check.True(field.Slider && field.Step == 1 && field.Min == min && field.Max == max, key + " must expose the original integer slider range");
            foreach (var input in new[] { min - 1, min, (min + max) / 2, max, max + 1, expectedDefault })
            {
                var draft = Importer.Import($"--scene uc3d --{key} {input.ToString(System.Globalization.CultureInfo.InvariantCulture)}", Draft()).Draft;
                Check.Close(Math.Clamp(Math.Truncate(input), min, max), (double)draft[key]!, 1e-9, key + " must retain and clamp original values");
                Check.Equal("original", (string)draft["uc3dControls"]!, key + " must select original units");
                var reimported = Importer.Import(Cmd(draft), Draft()).Draft;
                Check.True(JsonNode.DeepEquals(draft[key], reimported[key]), key + " must survive command export");
            }
            foreach (var invalid in new[] { "NaN", "Infinity", "broken", "0x10", "0b1100" })
            {
                var result = Importer.Import($"--scene uc3d --{key} {invalid}", Draft());
                Check.True(result.Ignored.Contains(key), key + " must reject " + invalid);
                Check.Close(expectedDefault, (double)result.Draft[key]!, 1e-9, key + " invalid input must retain its default");
            }
        }
    }

    private static void Uc3dControlInference()
    {
        foreach (var options in new[] { "--numColumns 120 --uc3dAmount 800", "--uc3dAmount 800 --numColumns 120" })
        {
            var original = Importer.Import("--scene uc3d " + options, Draft()).Draft;
            Check.Equal("original", (string)original["uc3dControls"]!, "native keys must win over inferred legacy mode");
            foreach (var explicitOptions in new[] { "--uc3dControls legacy " + options, options + " --uc3dControls legacy" })
            {
                var legacy = Importer.Import("--scene uc3d " + explicitOptions, Draft()).Draft;
                Check.Equal("legacy", (string)legacy["uc3dControls"]!, "explicit mode must win in either order");
            }
        }
        foreach (var key in Uc3dControlProfile.LegacyKeys)
        {
            var legacy = Importer.Import($"--{key} 1 --scene uc3d", Draft()).Draft;
            Check.Equal("legacy", (string)legacy["uc3dControls"]!, "generic " + key + " must preserve legacy commands");
        }
        foreach (var (key, requested) in new[] { ("cycleSpeed", "--cycleSpeed 0.2"), ("isometric", "--isometric true") })
        {
            foreach (var (options, expectedMode) in new[]
            {
                (requested, "legacy"),
                (requested + " --uc3dAmount 800", "original"),
                ("--uc3dAmount 800 " + requested, "original"),
                ("--uc3dControls original " + requested, "original"),
                (requested + " --uc3dControls original", "original"),
            })
            {
                var draft = Importer.Import("--scene uc3d " + options, Draft()).Draft;
                Check.Equal(expectedMode, (string)draft["uc3dControls"]!, key + " must respect native and explicit mode precedence in either order");
                foreach (var exported in new[] { Cmd(draft), Builder.BuildWebQueryString(draft) })
                {
                    var imported = Importer.Import(exported, Draft()).Draft;
                    Check.Equal(expectedMode, (string)imported["uc3dControls"]!, key + " mode must survive command and preview exports");
                    if (expectedMode == "legacy") Check.True(JsonNode.DeepEquals(draft[key], imported[key]), key + " requested value must survive legacy exports");
                }
            }
        }
        foreach (var (value, expected) in new[] { (-2.0, 0.0), (0.0, 0.0), (1.5, 1.5), (10.0, 4.0) })
        {
            var original = Importer.Import("--scene uc3d --animationSpeed " + value.ToString(System.Globalization.CultureInfo.InvariantCulture), Draft()).Draft;
            Check.Close(expected, (double)original["animationSpeed"]!, 1e-9, "original controls must accept only nonnegative animation multipliers");
        }
        var reverseLegacy = Importer.Import("--scene uc3d --uc3dControls legacy --animationSpeed -2", Draft()).Draft;
        Check.Close(-2, (double)reverseLegacy["animationSpeed"]!, 1e-9, "legacy controls must retain reverse animation");
    }

    private static void Uc3dInactiveUnits()
    {
        var original = Builder.CreateSceneDefaultDraft("uc3d");
        original["cycleSpeed"] = 0.987;
        original["isometric"] = true;
        original["numColumns"] = 123.0;
        original["density"] = 7.0;
        original["pictureScale"] = 2.0;
        original["uc3dAmount"] = 800.0;
        var before = original.DeepClone();
        foreach (var key in new[] { "cycleSpeed", "isometric" }.Concat(Uc3dControlProfile.LegacyKeys))
        {
            Check.DoesNotContain(Builder.BuildCommand(original, true, false), "--" + key + " ", "original exports must omit inactive " + key);
            Check.DoesNotContain(Builder.BuildWebQueryString(original), key + "=", "original preview must omit inactive " + key);
        }
        Check.Equal("original", (string)Importer.Import(Cmd(original), Draft()).Draft["uc3dControls"]!, "export must retain original mode");
        Check.True(JsonNode.DeepEquals(before, original), "export must preserve inactive values in the saved draft");
        original["uc3dControls"] = "legacy";
        foreach (var key in Uc3dControlProfile.NativeKeys)
            Check.DoesNotContain(Builder.BuildCommand(original, true, false), "--" + key + " ", "legacy exports must omit native " + key);
        var command = Cmd(original);
        Check.Contains(command, "--uc3dControls legacy", "legacy mode must be explicit even when generic values happen to match defaults");
        var restored = Importer.Import(command, Draft()).Draft;
        Check.Close(123, (double)restored["numColumns"]!, 1e-9, "legacy columns must survive");
        Check.Close(7, (double)restored["density"]!, 1e-9, "legacy density must survive");
    }

    private static void Uc3dVersion4Migration()
    {
        foreach (var edit in new[] { "none", "value", "name", "deleted", "draft" })
        {
            var values = Builder.CreateSceneDefaultDraft("uc3d");
            RemoveOriginalControls(values);
            values["bloomStrength"] = 0.7;
            if (edit == "value") values["fallSpeed"] = -3.75;
            var preset = new UserPreset { Id = "seed-uc3d-trilogy", Name = edit == "name" ? "My UC3D" : "UC3D Trilogy", Values = values };
            var lastDraft = StorageService.CloneObject(values);
            if (edit == "draft") lastDraft["density"] = 7.333;
            var state = new ConfiguratorState { PresetSeedVersion = 4, UserPresets = edit == "deleted" ? [] : [preset], SelectedPresetId = preset.Id, LastDraft = lastDraft };
            var saved = StorageService.CloneObject(values);
            saved["uc3dControls"] = "legacy";
            var expectedDraft = StorageService.CloneObject(lastDraft);
            expectedDraft["uc3dControls"] = "legacy";
            Check.True(PresetSeeder.Seed(state, Builder, Importer), "version 5 must run");
            if (edit is "none" or "draft")
                Check.True(JsonNode.DeepEquals(Builder.CreateSceneDefaultDraft("uc3d"), preset.Values), "untouched starter must use original controls");
            else if (edit != "deleted")
                Check.True(JsonNode.DeepEquals(saved, preset.Values), "custom starter must preserve its complete values");
            Check.True(JsonNode.DeepEquals(edit == "none" ? Builder.CreateSceneDefaultDraft("uc3d") : expectedDraft, state.LastDraft), "selected or custom draft must migrate deliberately: " + edit);
            if (edit == "deleted") Check.False(state.UserPresets.Any(candidate => candidate.Id == "seed-uc3d-trilogy"), "a deleted starter must remain deleted");
            Check.Equal(6, state.PresetSeedVersion, "seed version must advance once");
            Check.True(!PresetSeeder.Seed(state, Builder, Importer), "a second load must not migrate again");
        }
    }

    private static void Uc3dRandomization()
    {
        var original = Builder.CreateSceneDefaultDraft("uc3d");
        original["numColumns"] = 123.0;
        original["fallSpeed"] = -3.75;
        var randomized = new DraftRandomizer(1234).Randomize(original, "motion");
        Check.True(new[] { "uc3dAmount", "uc3dGlyphSize", "uc3dFallSpeed", "uc3dForwardSpeed" }.Any(key => !JsonNode.DeepEquals(original[key], randomized[key])), "motion randomization must change original sliders");
        foreach (var key in Uc3dControlProfile.LegacyKeys)
            Check.True(JsonNode.DeepEquals(original[key], randomized[key]), "randomization must retain inactive " + key);
        foreach (var key in new[] { "uc3dAmount", "uc3dGlyphSize", "uc3dFallSpeed", "uc3dForwardSpeed" })
        {
            var field = Metadata.Groups.SelectMany(group => group.Fields).Single(field => field.Id == key);
            var value = (double)randomized[key]!;
            Check.True(value >= field.Min && value <= field.Max && value == Math.Truncate(value), key + " randomized outside original slider units");
        }
        var colors = new DraftRandomizer(1234).Randomize(original, "colors");
        Check.True(!JsonNode.DeepEquals(original["codeColor"], colors["codeColor"]), "color randomization must change the active UC3D code color");
        original["uc3dControls"] = "legacy";
        var legacy = new DraftRandomizer(1234).Randomize(original, "motion");
        Check.True(!JsonNode.DeepEquals(original["numColumns"], legacy["numColumns"]), "legacy randomization must keep editing legacy columns");
        foreach (var key in Uc3dControlProfile.NativeKeys)
            Check.True(JsonNode.DeepEquals(original[key], legacy[key]), "legacy randomization must retain inactive " + key);
        var importedLegacy = Importer.Import("--scene uc3d --fallSpeed -3.75", Draft()).Draft;
        Check.Close(0.7, (double)importedLegacy["bloomStrength"]!, 1e-9, "legacy commands must retain the earlier bloom default");
        Check.DoesNotContain(Cmd(importedLegacy), "--bloomStrength", "legacy bloom at its default must remain compact");
    }
}
