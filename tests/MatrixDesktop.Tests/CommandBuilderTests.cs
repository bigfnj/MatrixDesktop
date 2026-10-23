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
        ("Window mode emits the matching wrapper flag", WindowModeFlags),
        ("A monitor index is dropped unless single monitor is selected", MonitorRequiresSingleMonitor),
        ("A test launch forces windowed and disables any-key exit", TestLaunchIsSafe),
        ("A test launch does not cover other windows or hide the cursor", TestLaunchIsInspectable),
        ("Shell significant characters are quoted", ShellQuoting),
        ("An empty value is quoted rather than dropped from the token list", EmptyValueQuoted),
        ("Generated arguments round trip through the importer", RoundTrip),
        ("A PowerShell export escapes single quotes by doubling", PowerShellQuoting),
        ("A PowerShell export references the script directory", PowerShellUsesScriptRoot),
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
    ];

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

    private static void PowerShellQuoting()
    {
        var d = Draft();
        d["effect"] = "image";
        d["url"] = "it's.png";
        var script = Builder.BuildPowerShellScript(d, false);
        Check.Contains(script, "'it''s.png'",
            "PowerShell single quoted strings escape an embedded quote by doubling it");
    }

    private static void PowerShellUsesScriptRoot()
    {
        var script = Builder.BuildPowerShellScript(Draft(), false);
        Check.Contains(script, "$PSScriptRoot",
            "the exported script is meant to be dropped beside the exe, so it must resolve relative to itself");
    }

    private static void PreviewQuerySkipsDefaults()
    {
        // stripePass.js uses an effect's built-in colours only when stripeColors is absent,
        // so emitting the default would flatten pride and trans into the same look.
        var d = Draft();
        d["effect"] = "pride";
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
        Check.Equal(1, state.UserPresets.Count, "migration updates the starter without duplicates");
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
            if (edit == "deleted") Check.Equal(0, state.UserPresets.Count, "a deleted starter must remain deleted");
            else if (edit != "unsaved draft") Check.True(JsonNode.DeepEquals(savedValues, previous.Values), "a user-owned starter must retain its values: " + edit);
            Check.True(JsonNode.DeepEquals(expectedDraft, state.LastDraft), "a custom unsaved draft must survive: " + edit);
            Check.Equal(edit == "name" ? "My UC3D" : "UC3D Trilogy", previous.Name, "preset names must survive migration");
        }
    }

    private static void RemoveOriginalControls(JsonObject draft)
    {
        draft.Remove("uc3dControls");
        foreach (var key in Uc3dControlProfile.NativeKeys) draft.Remove(key);
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
            if (edit == "deleted") Check.Equal(0, state.UserPresets.Count, "a deleted starter must remain deleted");
            Check.Equal(5, state.PresetSeedVersion, "seed version must advance once");
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
