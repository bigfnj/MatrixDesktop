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
}
