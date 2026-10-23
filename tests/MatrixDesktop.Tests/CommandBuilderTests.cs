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
        Check.True(PresetSeeder.Seed(state, Builder, Importer), "version 4 must run for the previous starter");
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
            var lastDraft = StorageService.CloneObject(previous.Values);
            lastDraft["numColumns"] = 111.0;
            var state = new ConfiguratorState
            {
                PresetSeedVersion = 3,
                UserPresets = edit == "deleted" ? [] : [previous],
                SelectedPresetId = previous.Id,
                LastDraft = StorageService.CloneObject(lastDraft),
            };
            Check.True(PresetSeeder.Seed(state, Builder, Importer), "version 4 advances even when no preset can change");
            if (edit == "deleted") Check.Equal(0, state.UserPresets.Count, "a deleted starter must remain deleted");
            else if (edit != "unsaved draft") Check.True(JsonNode.DeepEquals(savedValues, previous.Values), "a user-owned starter must retain its values: " + edit);
            Check.True(JsonNode.DeepEquals(lastDraft, state.LastDraft), "a custom unsaved draft must survive: " + edit);
            Check.Equal(edit == "name" ? "My UC3D" : "UC3D Trilogy", previous.Name, "preset names must survive migration");
        }
    }
}
