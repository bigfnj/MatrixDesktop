using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using MatrixDesktopConfigurator;

namespace MatrixDesktop.Tests;

internal static class ZMatrixTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("ZMatrix defaults and scene exports retain independent settings", DefaultsAndFiltering),
        ("ZMatrix structured arguments preserve JSON quotes spaces and backslashes", StructuredRoundTrip),
        ("ZMatrix raw command imports keep structured equals values paired", StructuredEqualsImport),
        ("ZMatrix explicit inline command imports preserve literal ampersands and percent signs", ExplicitInlineImport),
        ("ZMatrix bare structured imports distinguish literal values from real raw queries", BareStructuredImport),
        ("ZMatrix numeric controls clamp finite original units", NumericBounds),
        ("ZMatrix structured validators reject invalid glyph phrase and font payloads", StructuredLimits),
        ("ZMatrix CFG import maps source syntax and retains inactive settings", CfgImport),
        ("ZMatrix CFG import converts font DPI without rescaling glyph cleanup counts", CfgDpi),
        ("ZMatrix seed addition preserves frozen pre-ZMatrix UC3D starters and user edits", HistoricalMigration),
        ("ZMatrix wallpaper commands retain modes and test launches stay windowed", WallpaperCommands),
        ("ZMatrix randomization changes active controls and preserves private content", Randomization),
        ("ZMatrix retained CFG metadata survives storage without leaking into commands", RetainedMetadata),
        ("Screensaver profiles preserve argv and default to the original Matrix scene", ScreensaverArguments),
        ("Screensaver profiles reject malformed null and oversized synthetic payloads", ScreensaverInvalid),
        ("Structured command codec preserves Unicode and rejects malformed bounded input", StructuredCodec),
        ("ZMatrix copied and direct commands enforce their actual Windows length limits", CommandLimits),
    ];
    private static readonly ConfiguratorMetadata Metadata = ArgumentCatalog.Create();
    private static readonly CommandBuilder Builder = new(Metadata);
    private static readonly ArgumentImporter Importer = new(Metadata);
    private static JsonObject Draft() => Builder.CreateSceneDefaultDraft("zmatrix");
    private static ImportResult Import(string command, JsonObject? previous = null) => Importer.Import(command, previous ?? Builder.CreateDefaultDraft());
    private static double Num(JsonObject draft, string key) => double.Parse(draft[key]!.ToJsonString(), CultureInfo.InvariantCulture);

    private static void DefaultsAndFiltering()
    {
        var draft = Draft();
        Check.Equal("MatrixDesktop.exe --scene zmatrix", Builder.BuildCommand(draft, false, false), "new scene defaults must generate a compact command");
        draft["numColumns"] = 123.0; draft["codeColor"] = new JsonObject { ["r"] = .9, ["g"] = .2, ["b"] = .7 };
        draft["uc3dAmount"] = 731.0; draft["zmMaxStream"] = 1399.0;
        var before = draft.DeepClone();
        foreach (var command in new[] { Builder.BuildCommand(draft, true, false), Builder.BuildWebQueryString(draft) })
        {
            foreach (var inactive in new[] { "numColumns", "codeColor", "uc3dAmount", "animationSpeed", "font", "bloomStrength" })
                Check.False(command.Contains("--" + inactive + " ", StringComparison.Ordinal) || command.Contains(inactive + "=", StringComparison.Ordinal), inactive + " must not leak into ZMatrix exports");
            var restored = Import(command).Draft;
            Check.Close(1399, Num(restored, "zmMaxStream"), 0, "active stream settings must survive export");
        }
        Check.True(JsonNode.DeepEquals(before, draft), "command generation must not remove retained scene values");
        foreach (var scene in new[] { "matrix", "uc3d" })
        {
            draft["scene"] = scene;
            Check.DoesNotContain(Builder.BuildCommand(draft, true, false), "--zm", "ZMatrix options must not leak into " + scene);
            Check.DoesNotContain(Builder.BuildWebQueryString(draft), "zmMaxStream=", "preview must apply the same scene filtering");
        }
    }

    private static void StructuredRoundTrip()
    {
        var draft = Draft();
        draft["zmPhrases"] = new JsonArray("The \"matrix\" has C:\\code\\", "comma, semicolon; ampersand& percent% bang!", "Ω Ж カ");
        draft["zmCharacters"] = new JsonArray("A", "\"", "\\", "Ω", " ");
        draft["zmForegroundColor"] = ZMatrixControlProfile.RgbNode(1, 127, 255);
        var font = ZMatrixControlProfile.FontDefault();
        font["name"] = "Face \"quoted\""; font["height"] = -23;
        draft["zmFont"] = font;
        foreach (var command in new[] { Builder.BuildCommand(draft, false, false), Builder.BuildWebQueryString(draft) })
        {
            var result = Import(command);
            foreach (var key in new[] { "zmPhrases", "zmCharacters", "zmFont", "zmForegroundColor" })
                Check.True(JsonNode.DeepEquals(draft[key], result.Draft[key]), key + " must survive generated command and query imports");
        }
        var commandTokens = Tokenizer.Tokenize(Builder.BuildCommand(draft, false, false)).Skip(1).ToArray();
        var decodedTokens = commandTokens.Select(token => global::Shared.StructuredArgumentCodec.TryDecode(token, out var decoded) ? decoded : token);
        Check.True(decodedTokens.SequenceEqual(Builder.BuildArguments(draft, false, false)), "decoded display command tokens must equal the raw launch argument list");
        Check.Contains(Builder.BuildCommand(draft, false, false), "json64:", "copied structured arguments must use the shell-neutral codec");
        Check.False(Builder.BuildArguments(draft, false, false).Any(token => token.StartsWith("json64:", StringComparison.Ordinal)), "direct launch must retain raw structured argv");
        var pathDraft = Builder.CreateDefaultDraft();
        pathDraft["url"] = "C:\\a folder\\with\\a \"quote\"\\";
        var parsed = Import(Builder.BuildCommand(pathDraft, false, false)).Draft;
        Check.True(JsonNode.DeepEquals(pathDraft["url"], parsed["url"]), "Windows quoting must preserve ordinary and trailing path backslashes");
    }

    private static void StructuredEqualsImport()
    {
        var result = Import("""--scene zmatrix --zmPhrases "[\"A=B & C\",\"\\\"quoted\\\"\",\"C:\\path\\file\",\"Ω\"]" --zmFont "{\"name\":\"Face = C\",\"height\":-21}" """);
        Check.True(JsonNode.DeepEquals(new JsonArray("A=B & C", "\"quoted\"", "C:\\path\\file", "Ω"), result.Draft["zmPhrases"]), "raw JSON phrases must remain paired through command tokenization");
        Check.Equal("Face = C", result.Draft["zmFont"]!["name"]!.GetValue<string>(), "raw JSON font names must retain equals signs");
        Check.Equal(0, result.Ignored.Length, "valid structured payloads must not become separate ignored keys");
        var invalid = Import("""--scene zmatrix --zmPhrases "[\"A=B\"" """);
        Check.True(invalid.Ignored.SequenceEqual(new[] { "zmPhrases" }), "malformed JSON remains paired for typed validation");
    }

    private static void ExplicitInlineImport()
    {
        foreach (var prefix in new[] { "--", "/" })
        {
            var command = "--scene zmatrix \"" + prefix + "zmPhrases=[\\\"A=B&C%2F\\\"]\" \"" + prefix + "zmFont={\\\"name\\\":\\\"Face = & %2F\\\",\\\"height\\\":-21}\"";
            var result = Import(command);
            Check.Equal("A=B&C%2F", result.Draft["zmPhrases"]![0]!.GetValue<string>(), "explicit inline JSON must not be split or URL-decoded");
            Check.Equal("Face = & %2F", result.Draft["zmFont"]!["name"]!.GetValue<string>(), "explicit inline font payload must remain literal");
            Check.Equal(0, result.Ignored.Length, "explicit structured keys must be recognized");
            var directory = Import("--scene uc3d \"" + prefix + "pictureDirectory=C:\\A&B%2F\"");
            Check.Equal("C:\\A&B%2F", directory.Draft["pictureDirectory"]!.GetValue<string>(), "explicit non-JSON values must also remain literal rather than becoming raw queries");
        }
    }

    private static void BareStructuredImport()
    {
        var literal = Import("""--scene zmatrix "zmPhrases=[\"A&B%2F\"]" """);
        Check.Equal("A&B%2F", literal.Draft["zmPhrases"]![0]!.GetValue<string>(), "a complete bare structured pair is one literal value");
        var query = Import("""--scene zmatrix "zmPhrases=[\"A\"]&zmMaxStream=321" """);
        Check.Equal("A", query.Draft["zmPhrases"]![0]!.GetValue<string>(), "a real raw query retains its structured first value");
        Check.Close(321, Num(query.Draft, "zmMaxStream"), 0, "a real raw query must still split later pairs");
        var encoded = Import("?scene=zmatrix&zmPhrases=%5B%22A%3DB%26C%22%5D&zmMaxStream=456");
        Check.Equal("A=B&C", encoded.Draft["zmPhrases"]![0]!.GetValue<string>(), "explicit raw queries continue decoding structured values");
        Check.Close(456, Num(encoded.Draft, "zmMaxStream"), 0, "encoded queries retain subsequent fields");
    }

    private static void NumericBounds()
    {
        foreach (var field in Metadata.Groups.SelectMany(group => group.Fields).Where(field => field.Id.StartsWith("zm", StringComparison.Ordinal) && field.Kind == "number"))
        {
            foreach (var value in new[] { field.Min!.Value - 1, field.Min.Value, (field.Min.Value + field.Max!.Value) / 2, field.Max.Value, field.Max.Value + 1 })
            {
                var result = Import("--scene zmatrix --" + field.Id + " " + value.ToString(CultureInfo.InvariantCulture));
                var expected = Math.Clamp(field.Slider ? Math.Truncate(value) : value, field.Min.Value, field.Max!.Value);
                Check.Close(expected, Num(result.Draft, field.Id), 0, field.Id + " must clamp original units");
            }
            foreach (var invalid in new[] { "NaN", "Infinity", "-Infinity", "1e5000", "0x20" })
            {
                var result = Import("--scene zmatrix --" + field.Id + " " + invalid);
                Check.True(result.Ignored.Contains(field.Id), field.Id + " must reject nonfinite or malformed input");
            }
        }
        var rgb = Import("--scene zmatrix --zmForegroundColor -5,127.5,999").Draft["zmForegroundColor"]!;
        Check.Equal("{\"r\":0,\"g\":128,\"b\":255}", rgb.ToJsonString(), "byte colors round and clamp without normalized-color conversion");
    }

    private static void StructuredLimits()
    {
        var fields = Metadata.Groups.SelectMany(group => group.Fields).ToDictionary(field => field.Id);
        foreach (var (key, value) in new[]
        {
            ("zmCharacters", "[]"), ("zmCharacters", "[\"AB\"]"), ("zmCharacters", "[\"\\ud83d\\ude00\"]"), ("zmCharacters", "[\"\\n\"]"),
            ("zmCharacters", new JsonArray(Enumerable.Repeat("A", 513).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()).ToJsonString()),
            ("zmPhrases", "[\"bad\\u0000phrase\"]"), ("zmPhrases", "[\"" + new string('A', 257) + "\"]"),
            ("zmPhrases", new JsonArray(Enumerable.Repeat("phrase", 65).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()).ToJsonString()),
            ("zmFont", "{\"height\":\"NaN\"}"), ("zmFont", "{\"name\":\"\"}"), ("zmFont", "{\"name\":\"" + new string('A', 32) + "\"}"),
        })
            Check.True(ZMatrixControlProfile.ParseStructured(fields[key], value) is null, key + " must reject " + value[..Math.Min(value.Length, 48)]);
        Check.Equal("[]", ZMatrixControlProfile.ParseStructured(fields["zmPhrases"], "[]")!.ToJsonString(), "empty phrases must explicitly disable phrase content");
        Check.Equal("[\"A\",\"B\"]", ZMatrixControlProfile.ParseStructured(fields["zmCharacters"], "[\"A\",\"A\",\"B\"]")!.ToJsonString(), "duplicate characters must retain their first occurrence");
        var payload = "{\"height\":-999,\"width\":999,\"family\":47,\"weight\":2000,\"italic\":true}";
        var font = (JsonObject)ZMatrixControlProfile.ParseStructured(fields["zmFont"], payload)!;
        Check.Equal(-256, font["height"]!.GetValue<int>(), "font bounds must clamp");
        Check.Equal(256, font["width"]!.GetValue<int>(), "width must clamp");
        Check.Equal(32, font["family"]!.GetValue<int>(), "family keeps only its high nibble");
        Check.Equal(1000, font["weight"]!.GetValue<int>(), "weight must clamp");
    }

    private static void CfgImport()
    {
        const string cfg = """
            [General]
            ConfigFileFormatVersion=1.1
            MaxStream=1234
            SpeedVariance=9
            RefreshTime=40
            MonotonousCleanupEnabled=1
            BackTrace=71
            RandomizedCleanupEnabled=1
            Leading=19
            SpacePad=37
            SpecialStringStreamProbability=.25
            PriorityClass=IDLE_PRIORITY_CLASS
            CoeffR1=2
            [Text]
            FontHeight=-11
            FontName=Consolas
            FontWeight=FW_BOLD
            FontCharSet=GREEK_CHARSET
            FontClipPrecision=CLIP_LH_ANGLES|CLIP_EMBEDDED
            FontItalic=1
            CharSet=A, 0x42, 0103, ' ', Ω, ABC
            [Colors]
            FGColor={1,2,3,128}
            FadeColor={4,5,6,255}
            BGColor={7,8,9,128}
            BGMode=bgmodeColor
            BlendMode=blendmodeXOR
            SpecialStringFGColor={10,11,12,255}
            SpecialStringFadeColor={13,14,15,128}
            SpecialStringBGColor={9,9,9,255}
            [SpecialText]
            FontName=Terminal
            FontHeight=-17
            Strings=First|Second;Third\nFourth|First
            """;
        var previous = Builder.CreateDefaultDraft();
        previous["windowMode"] = "single-monitor"; previous["monitor"] = 2.0; previous["processPriority"] = "normal";
        var result = Import(cfg, previous);
        Check.Equal("zmatrix", result.Draft["scene"]!.GetValue<string>(), "CFG import must select the new scene");
        Check.Equal("single-monitor", result.Draft["windowMode"]!.GetValue<string>(), "visual CFG import must preserve the selected launch mode");
        Check.Close(2, Num(result.Draft, "monitor"), 0, "monitor selection must survive CFG import");
        Check.Equal("normal", result.Draft["processPriority"]!.GetValue<string>(), "an explicit launch priority must survive CFG import");
        Check.Close(1234, Num(result.Draft, "zmMaxStream"), 0, "source integer settings map to their own units");
        Check.Equal("[\"A\",\"B\",\"C\",\" \",\"Ω\"]", result.Draft["zmCharacters"]!.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), "source character tokens preserve literal, hex, octal and quoted-space forms");
        Check.Equal("[\"First\",\"Second\",\"Third\",\"Fourth\"]", result.Draft["zmPhrases"]!.ToJsonString(), "source phrase delimiters and deduplication must match");
        var font = (JsonObject)result.Draft["zmFont"]!;
        Check.Equal(700, font["weight"]!.GetValue<int>(), "symbolic weights must map");
        Check.Equal(161, font["charSet"]!.GetValue<int>(), "symbolic charsets must map");
        Check.Equal(144, font["clipPrecision"]!.GetValue<int>(), "bitwise constant combinations must map");
        Check.True(result.Draft["zmOpaqueBackground"]!.GetValue<bool>(), "native background alpha threshold must map");
        Check.True(result.Notes.Any(note => note.Contains("alpha retained", StringComparison.Ordinal)), "ignored alpha must be visibly reported");
        Check.True(result.Ignored.Contains("General.CoeffR1"), "API-only coefficients must not pretend to be recognized CFG settings");
        Check.True(result.Draft["zmImportedSettings"]?["General.CoeffR1"] is not null, "inactive settings must remain reviewable");
    }

    private static void CfgDpi()
    {
        foreach (var cfg in new[]
        {
            "[General]\nConfigDPI=144\nBackTrace=40\nLeading=10\nSpacePad=30\n[Text]\nFontHeight=-24\nFontWidth=12",
            "[Text]\nFontHeight=-24\nFontWidth=12\n[General]\nConfigDPI=144\nBackTrace=40\nLeading=10\nSpacePad=30",
        })
        {
            var result = Import(cfg);
            var font = (JsonObject)result.Draft["zmFont"]!;
            Check.Equal(-16, font["height"]!.GetValue<int>(), "source font pixels must normalize to96DPI regardless of section order");
            Check.Equal(8, font["width"]!.GetValue<int>(), "source font width must normalize");
            Check.Close(40, Num(result.Draft, "zmBackTrace"), 0, "cleanup backtrace remains a glyph count");
            Check.Close(10, Num(result.Draft, "zmLeading"), 0, "leading remains a glyph count");
            Check.Close(30, Num(result.Draft, "zmSpacePad"), 0, "padding remains a glyph count");
            Check.True(result.Notes.Any(note => note.Contains("extra DPI scaling", StringComparison.Ordinal)), "the intentional count correction must be reported");
        }
    }

    private static void HistoricalMigration()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HistoricalDraftV5.json")!;
        using var reader = new StreamReader(stream);
        var historical = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        Check.False(historical.ContainsKey("zmFont") || historical.ContainsKey("processPriority"), "fixture must actually predate new fields");
        foreach (var version in new[] { 3, 4 })
        foreach (var edited in new[] { false, true })
        {
            var values = (JsonObject)historical.DeepClone();
            values.Remove("uc3dControls"); foreach (var key in Uc3dControlProfile.NativeKeys) values.Remove(key);
            values["scene"] = "uc3d"; values["volumetric"] = true; values["randomForwardMovement"] = true;
            values["pictureDelay"] = 60.0; values["pictureDuration"] = 30.0;
            if (version == 3)
            {
                values["codeColor"] = new JsonObject { ["r"] = 0.0, ["g"] = .698, ["b"] = 0.0 };
                values["cursorColor"] = new JsonObject { ["r"] = 0.0, ["g"] = 1.0, ["b"] = .698 };
            }
            else
            {
                foreach (var (key, value) in Metadata.SceneDefaults["uc3d"])
                    if (values.ContainsKey(key)) values[key] = System.Text.Json.JsonSerializer.SerializeToNode(value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                values["bloomStrength"] = .7;
            }
            if (edited) values["ownerExtension"] = "must survive";
            var preset = new UserPreset { Id = "seed-uc3d-trilogy", Name = "UC3D Trilogy", Values = values };
            var state = new ConfiguratorState { PresetSeedVersion = version, UserPresets = [preset], SelectedPresetId = preset.Id, LastDraft = (JsonObject)values.DeepClone() };
            PresetSeeder.Seed(state, Builder, Importer);
            Check.Equal(edited ? "legacy" : "original", preset.Values["uc3dControls"]!.GetValue<string>(), "frozen v" + version + " starter migration must preserve exact-edit semantics");
            if (edited) Check.Equal("must survive", preset.Values["ownerExtension"]!.GetValue<string>(), "unknown user-owned fields must not disappear");
            Check.Equal(1, state.UserPresets.Count(item => item.Id == "seed-zmatrix-desktop"), "the new starter must be added once");
            var zmatrix = state.UserPresets.Single(item => item.Id == "seed-zmatrix-desktop");
            Check.Equal("wallpaper", zmatrix.Values["windowMode"]!.GetValue<string>(), "the desktop starter must explicitly choose wallpaper");
            Check.False(PresetSeeder.Seed(state, Builder, Importer), "second seeding must be inert");
            state.UserPresets.Remove(zmatrix);
            Check.False(PresetSeeder.Seed(state, Builder, Importer), "deleted new starter must stay deleted");
        }
    }

    private static void WallpaperCommands()
    {
        var draft = Draft();
        draft["windowMode"] = "wallpaper"; draft["wallpaperInput"] = true; draft["wallpaperPauseFullscreen"] = false; draft["wallpaperPauseBattery"] = true;
        draft["processPriority"] = "below-normal"; draft["topmost"] = true; draft["workingArea"] = true;
        var command = Builder.BuildCommand(draft, false, false);
        Check.Contains(command, "--wallpaper", "wallpaper mode must emit the wrapper flag");
        Check.Contains(command, "--wallpaper-input", "wallpaper mouse interaction must be explicit");
        Check.Contains(command, "--no-wallpaper-pause-fullscreen", "false wallpaper defaults must emit their negative flag");
        Check.Contains(command, "--process-priority below-normal", "priority must emit the agreed wrapper option");
        Check.DoesNotContain(command, "--topmost", "wallpaper cannot inherit foreground-only behavior");
        Check.DoesNotContain(command, "--working-area", "wallpaper uses full desktop bounds");
        var host = AppCli.Parse(Builder.BuildArguments(draft, false, false).ToArray(), out _);
        Check.Equal(WindowMode.Wallpaper, host.WindowMode, "the real wrapper must accept the generated wallpaper mode");
        Check.Equal("below-normal", host.ProcessPriority, "the real wrapper must accept the generated priority value");
        Check.True(host.WallpaperInput && !host.WallpaperPauseFullscreen && host.WallpaperPauseBattery, "wrapper and importer must agree on positive and negative wallpaper switches");
        var restored = Import(command).Draft;
        foreach (var key in new[] { "windowMode", "wallpaperInput", "wallpaperPauseFullscreen", "wallpaperPauseBattery", "processPriority" })
            Check.True(JsonNode.DeepEquals(draft[key], restored[key]), key + " must roundtrip");
        var testCommand = Builder.BuildCommand(draft, true, true);
        Check.Contains(testCommand, "--windowed", "Test Argument must remain inspectable");
        Check.DoesNotContain(testCommand, "--wallpaper", "Test Argument must not change desktop hosting");
        Check.DoesNotContain(testCommand, "--process-priority", "Test Argument must not inherit realtime or high priority");
        Check.Equal("windowed", Import("--wallpaper --windowed").Draft["windowMode"]!.GetValue<string>(), "last mode wins");
        Check.Equal("wallpaper", Import("--windowed --wallpaper").Draft["windowMode"]!.GetValue<string>(), "wallpaper also wins when last");
    }

    private static void Randomization()
    {
        var draft = Draft();
        draft["zmPhrases"] = new JsonArray("owner text");
        draft["zmCharacters"] = new JsonArray("Ω", " ");
        draft["zmImportedSettings"] = new JsonObject { ["unknown"] = "preserve" };
        draft["numColumns"] = 137.0; draft["windowMode"] = "wallpaper";
        foreach (var scope in new[] { "visual", "motion", "colors" })
        {
            var randomized = new DraftRandomizer(1234).Randomize(draft, scope);
            foreach (var key in new[] { "zmPhrases", "zmCharacters", "zmFont", "zmSpecialFont", "zmImportedSettings", "numColumns", "windowMode" })
                Check.True(JsonNode.DeepEquals(draft[key], randomized[key]), scope + " must preserve " + key);
            Check.True((scope == "colors" ? new[] { "zmForegroundColor", "zmSpecialForegroundColor" } : new[] { "zmMaxStream", "zmRefreshTime", "zmSpeedVariance" }).Any(key => !JsonNode.DeepEquals(draft[key], randomized[key])), scope + " must change controls used by the active renderer");
        }
    }

    private static void RetainedMetadata()
    {
        var result = Import("[General]\nMaxStream=777\nUnsupportedOwnerSetting=keep\n[Colors]\nSpecialStringBGColor={1,2,3,255}");
        foreach (var command in new[] { Builder.BuildCommand(result.Draft, true, false), Builder.BuildWebQueryString(result.Draft) })
            Check.False(command.Contains("zmImportedSettings", StringComparison.Ordinal) || command.Contains("UnsupportedOwnerSetting", StringComparison.Ordinal), "retained settings are review metadata, not working controls");
        var directory = Path.Combine(Path.GetTempPath(), "matrix-zmatrix-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new StorageService(Path.Combine(directory, "presets.json"));
            storage.Save(new ConfiguratorState { PresetSeedVersion = 6, LastDraft = result.Draft });
            Check.True(JsonNode.DeepEquals(result.Draft, storage.Load().LastDraft), "retained source settings and notes must survive storage");
        }
        finally { RemoveFixture(directory); }
    }

    private static void ScreensaverArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "matrix-zmatrix-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "screensaver.json");
        try
        {
            Check.True(global::Shared.ScreensaverProfile.LoadFrom(path).SequenceEqual(new[] { "--scene", "matrix" }), "missing profiles must preserve the original scene default");
            var draft = Draft(); draft["zmPhrases"] = new JsonArray("A \"quote\" and C:\\path\\");
            var arguments = Builder.BuildArguments(draft, true, false).ToArray();
            global::Shared.ScreensaverProfile.SaveTo(path, arguments);
            Check.True(arguments.SequenceEqual(global::Shared.ScreensaverProfile.LoadFrom(path)), "profile storage must preserve argv instead of rebuilding a quoted shell command");
            Check.False(File.Exists(path + ".tmp"), "successful profile saving must not leave the temporary file");
        }
        finally { RemoveFixture(directory); }
    }

    private static void ScreensaverInvalid()
    {
        var directory = Path.Combine(Path.GetTempPath(), "matrix-zmatrix-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "screensaver.json");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var invalid in new[] { "null", "[null]", "{\"scene\":\"matrix\"}", "not json" })
            {
                File.WriteAllText(path, invalid);
                var rejected = false;
                try { _ = global::Shared.ScreensaverProfile.LoadFrom(path); }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException) { rejected = true; }
                Check.True(rejected, "invalid profile data must fail before launching an invented argument list: " + invalid);
            }
            File.WriteAllText(path, new string(' ', 1_048_577));
            var oversized = false;
            try { _ = global::Shared.ScreensaverProfile.LoadFrom(path); }
            catch (InvalidDataException) { oversized = true; }
            Check.True(oversized, "oversized profile data must fail with its explicit size guard");
            var invalidArgument = false;
            try { global::Shared.ScreensaverProfile.SaveTo(path, [null!]); }
            catch (ArgumentException) { invalidArgument = true; }
            Check.True(invalidArgument, "null argv entries must be rejected while saving");
            var oversizedSave = false;
            try { global::Shared.ScreensaverProfile.SaveTo(path, [new string('A', 1_048_577)]); }
            catch (ArgumentException) { oversizedSave = true; }
            Check.True(oversizedSave, "saving must enforce the same bounded profile format");
        }
        finally { RemoveFixture(directory); }
    }

    private static void RemoveFixture(string directory)
    {
        var full = Path.GetFullPath(directory);
        var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Check.True(string.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("matrix-zmatrix-tests-", StringComparison.Ordinal), "cleanup must remain in this test's owned synthetic temporary directory");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

    private static void StructuredCodec()
    {
        foreach (var text in new[] { "[\"Ω Ж カ\", \"%PATH% & quote\\\" backslash\\\\ bang!\"]", "{\"name\":\"Consolas\",\"height\":-21}" })
        {
            var encoded = global::Shared.StructuredArgumentCodec.Encode(text);
            Check.True(encoded.StartsWith("json64:", StringComparison.Ordinal) && encoded["json64:".Length..].All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'), "copied payload must contain only shell-neutral characters");
            Check.True(global::Shared.StructuredArgumentCodec.TryDecode(encoded, out var decoded) && decoded == text, "strict UTF8 codec must preserve content exactly");
            Check.True(global::Shared.StructuredArgumentCodec.TryDecode(text, out decoded) && decoded == text, "raw JSON must remain accepted");
        }
        foreach (var invalid in new[] { "json64:A", "json64:@@", "json64:YWJj=", "json64:_w", "json64:wK8", "json64:" + new string('A', 90_000), new string('Ω', 32_769) })
            Check.False(global::Shared.StructuredArgumentCodec.TryDecode(invalid, out _), "codec must reject malformed, noncanonical, invalid-UTF8 or oversized payloads");
        var oversizedDecoded = "json64:" + Convert.ToBase64String(new byte[65_537]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Check.False(global::Shared.StructuredArgumentCodec.TryDecode(oversizedDecoded, out _), "the decoded byte bound must reject input below the encoded-size ceiling");
        Check.True(global::Shared.StructuredArgumentCodec.TryDecode(global::Shared.StructuredArgumentCodec.Encode(new string('A', 65_536)), out var boundary) && boundary.Length == 65_536, "the exact64KB boundary must remain usable");
        var rejected = false;
        try { _ = global::Shared.StructuredArgumentCodec.Encode(new string('A', 65_537)); }
        catch (ArgumentException) { rejected = true; }
        Check.True(rejected, "encoding must enforce the same64KB limit");
    }

    private static void CommandLimits()
    {
        var draft = Draft();
        draft["zmPhrases"] = new JsonArray(Enumerable.Range(0, 64).Select(index => (JsonNode?)JsonValue.Create(index.ToString(CultureInfo.InvariantCulture) + new string('Ω', 250))).ToArray());
        var copyRejected = false;
        try { _ = Builder.BuildCommand(draft, false, false); }
        catch (InvalidOperationException error) { copyRejected = error.Message.Contains("32,767", StringComparison.Ordinal); }
        Check.True(copyRejected, "large Unicode content must report that its encoded copied command exceeds the Windows limit");
        var arguments = Builder.BuildArguments(draft, false, false);
        Check.True(arguments.Contains("--zmPhrases") && arguments.All(argument => !argument.Contains("\\u03A9", StringComparison.OrdinalIgnoreCase)), "direct argv must keep valid BMP text compact instead of needlessly expanding it");
        Check.True(Builder.BuildTestCommandDescription(draft).Contains("direct structured launch", StringComparison.Ordinal), "a valid direct Test launch must not fail while formatting an oversized copied-command reply");
        Check.True(Builder.BuildPowerShellScript(draft, false).All(character => character <= 127), "PowerShell export must remain encoding-independent for Windows PowerShell 5.1");
        draft["zmPhrases"] = new JsonArray(new string('A', 33_000));
        var launchRejected = false;
        try { _ = Builder.BuildArguments(draft, false, false); }
        catch (InvalidOperationException error) { launchRejected = error.Message.Contains("32,767", StringComparison.Ordinal); }
        Check.True(launchRejected, "direct launch must also reject an actual overlong argument list");
    }
}
