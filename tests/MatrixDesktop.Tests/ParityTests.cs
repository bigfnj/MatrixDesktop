using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatrixDesktopConfigurator;

namespace MatrixDesktop.Tests;

// The two guards in this file are worth more than the rest of the suite. Both lock down a
// duplication that the codebase already tried and failed to eliminate: Shared/FlagNormalization
// carries a comment claiming it keeps alias handling in one place so adding an alias is "a
// one-line change instead of two", but AppCli and ArgumentImporter still each own a separate
// switch statement with a different spelling convention. Nothing enforced agreement until now.
internal static class ParityTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("Every wrapper flag the app accepts is also accepted by the configurator importer", AliasParity),
        ("The canonical alias list matches AppCli.Parse's actual switch labels", AliasListIsNotStale),
        ("The randomizer's colour maths agrees with ColorConverter", HslEquivalence),
        ("FlagNormalization parses every documented boolean spelling", BooleanParsing),
        ("FlagNormalization canonicalises keys consistently", KeyNormalisation),
        ("A seeded randomizer is reproducible", RandomizerIsSeedable),
        ("Randomizing never selects an effect the configurator cannot preview", RandomizerAvoidsUnsafeEffects),
    ];

    // Aliases that AppCli.Parse handles but that this list deliberately omits, each with the
    // reason. Anything added here is exempt from AliasListIsNotStale, so keep it short.
    private static readonly string[] AliasesExcludedFromParity =
    [
        // Consumes the NEXT token as its value ("--monitor 2"), so the "handled means it
        // forwarded nothing" heuristic in AliasParity cannot express it. Covered instead by
        // AppCliTests' monitor-parsing cases.
        "monitor",
    ];

    // Canonical alias list, mirroring AppCli.Parse's switch labels. AliasListIsNotStale
    // holds this equal to the real labels, so adding a flag to the app without adding it
    // here fails, and AliasParity then forces the importer to accept it too.
    private static readonly string[] WrapperAliases =
    [
        "windowed",
        "borderless", "fullscreen", "span", "span-all", "spanall",
        "single-monitor", "singlemonitor",
        "working-area", "workingarea",
        "topmost", "no-topmost", "notopmost",
        "exit-on-esc", "esc-exit", "exitonesc", "escexit",
        "no-esc-exit", "noesc-exit", "no-esc",
        "exit-on-any-key", "exit-on-anykey", "anykey-exit",
        "no-exit-on-any-key", "no-anykey-exit",
        "global-key-exit", "globalkey-exit", "global-exit-on-key", "background-key-exit",
        "foreground-key-exit", "foregroundkey-exit", "require-foreground-key-exit",
        "requireforeground-key-exit", "no-global-key-exit", "noglobal-key-exit",
        "hide-cursor", "hidecursor",
        "show-cursor", "showcursor",
        "no-devtools", "nodevtools", "devtools",
    ];

    // Reads the AppCli.cs source embedded by the csproj and compares its switch labels to
    // WrapperAliases. This is what makes AliasParity enforcing rather than decorative: the
    // list above used to be a hand copy, so a new case in AppCli.Parse could be handled by
    // the app, unknown to the importer, and still show a green suite.
    //
    // AppCli.cs contains exactly one switch and one default:, so every `case "..."` in the
    // file belongs to it. If that ever stops being true this test starts over-reporting,
    // which fails loudly rather than silently passing.
    private static void AliasListIsNotStale()
    {
        using var stream = typeof(ParityTests).Assembly.GetManifestResourceStream("AppCli.cs.txt")
            ?? throw new InvalidOperationException(
                "AppCli.cs.txt is not embedded. The EmbeddedResource item in MatrixDesktop.Tests.csproj " +
                "was removed or its LogicalName changed, which would silently reduce this test to a no-op.");
        var source = new StreamReader(stream).ReadToEnd();

        var labels = Regex.Matches(source, "^\\s*case \"([^\"]+)\":", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Except(AliasesExcludedFromParity, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Check.True(labels.Length > 30,
            $"only found {labels.Length} case labels in the embedded AppCli.cs, so the regex no longer " +
            "matches the source and this test would pass against almost nothing");

        Check.SetEqual(labels, WrapperAliases,
            "ParityTests.WrapperAliases has drifted from AppCli.Parse. Add the new alias to the list " +
            "(and to ArgumentImporter, which AliasParity will then require).");
    }

    private static void AliasParity()
    {
        var metadata = ArgumentCatalog.Create();
        var builder = new CommandBuilder(metadata);
        var importer = new ArgumentImporter(metadata);

        var appRejected = new List<string>();
        var importerRejected = new List<string>();

        foreach (var alias in WrapperAliases)
        {
            // The app recognises a flag when it consumes it rather than forwarding it to
            // the web layer.
            AppCli.Parse(["--" + alias], out var passthrough);
            if (passthrough.Count != 0) appRejected.Add(alias);

            // The importer recognises a flag when it applies at least one draft field.
            var result = importer.Import("--" + alias, builder.CreateDefaultDraft());
            if (result.Applied.Length == 0) importerRejected.Add(alias);
        }

        Check.Equal(0, appRejected.Count,
            $"AppCli forwarded these instead of handling them, so the canonical list is stale: [{string.Join(", ", appRejected)}]");
        Check.Equal(0, importerRejected.Count,
            "the configurator importer does not recognise every flag the app does, so importing a command " +
            $"would silently drop settings: [{string.Join(", ", importerRejected)}]");
    }

    private static void HslEquivalence()
    {
        // Sampled grid rather than a couple of spot values, because the two implementations
        // are separate copies of the same piecewise function and a divergence could hide in
        // any one of its four branches.
        var checkedCount = 0;
        for (var h = 0.0; h < 1.0; h += 0.05)
        {
            foreach (var s in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                foreach (var l in new[] { 0.0, 0.1, 0.3, 0.5, 0.7, 0.9, 1.0 })
                {
                    var viaConverter = ColorConverter.HslToRgb(h, s, l);
                    var viaRandomizer = DraftRandomizer.ColorFromHsl(h, s, l);

                    // The randomizer rounds to three decimals for a tidy generated command,
                    // so compare at that resolution.
                    Check.Close(Math.Round(viaConverter[0], 3), (double)viaRandomizer["r"]!, 5e-4,
                        $"red diverges at h={h:F2} s={s} l={l}");
                    Check.Close(Math.Round(viaConverter[1], 3), (double)viaRandomizer["g"]!, 5e-4,
                        $"green diverges at h={h:F2} s={s} l={l}");
                    Check.Close(Math.Round(viaConverter[2], 3), (double)viaRandomizer["b"]!, 5e-4,
                        $"blue diverges at h={h:F2} s={s} l={l}");
                    checkedCount++;
                }
            }
        }
        Check.True(checkedCount >= 700, $"the grid should cover hundreds of points, covered {checkedCount}");
    }

    private static void BooleanParsing()
    {
        foreach (var v in new[] { "1", "true", "TRUE", "y", "yes", "on", "  on  " })
        {
            Check.True(MatrixDesktop.Shared.FlagNormalization.ParseBool(v, false), $"'{v}' is a documented truthy spelling");
        }
        foreach (var v in new[] { "0", "false", "FALSE", "n", "no", "off" })
        {
            Check.False(MatrixDesktop.Shared.FlagNormalization.ParseBool(v, true), $"'{v}' is a documented falsy spelling");
        }
        Check.True(MatrixDesktop.Shared.FlagNormalization.ParseBool(null, true), "a missing value falls back to the caller's default");
        Check.True(MatrixDesktop.Shared.FlagNormalization.ParseBool("   ", true), "a blank value falls back to the caller's default");
    }

    private static void KeyNormalisation()
    {
        Check.Equal("exit-on-esc", MatrixDesktop.Shared.FlagNormalization.NormalizeKey("--exit_on_esc".TrimStart('-')),
            "underscores are treated as hyphens so both spellings reach the same switch label");
        Check.Equal("window", MatrixDesktop.Shared.FlagNormalization.NormalizeKey("  Window  "),
            "keys are trimmed and lowercased");
        Check.Equal(string.Empty, MatrixDesktop.Shared.FlagNormalization.NormalizeKey(null),
            "a null key must not throw");
    }

    private static void RandomizerIsSeedable()
    {
        var metadata = ArgumentCatalog.Create();
        var builder = new CommandBuilder(metadata);

        var a = new DraftRandomizer(1234).Randomize(builder.CreateDefaultDraft(), "visual");
        var b = new DraftRandomizer(1234).Randomize(builder.CreateDefaultDraft(), "visual");
        Check.Equal(a.ToJsonString(), b.ToJsonString(), "the same seed must produce the same draft, or nothing about it is testable");

        var c = new DraftRandomizer(9999).Randomize(builder.CreateDefaultDraft(), "visual");
        Check.True(a.ToJsonString() != c.ToJsonString(), "a different seed should produce a different draft");
    }

    private static void RandomizerAvoidsUnsafeEffects()
    {
        var metadata = ArgumentCatalog.Create();
        var builder = new CommandBuilder(metadata);
        var forbidden = new[] { "mirror", "image", "none" };

        for (var seed = 0; seed < 200; seed++)
        {
            var draft = new DraftRandomizer(seed).Randomize(builder.CreateDefaultDraft(), "visual");
            var effect = (draft["effect"] as JsonValue)?.GetValue<string>() ?? string.Empty;
            Check.False(forbidden.Contains(effect),
                $"seed {seed} produced '{effect}', but mirror needs a webcam, image needs a URL and none is a debug view");
            Check.False((draft["camera"] as JsonValue)?.GetValue<bool>() ?? false,
                $"seed {seed} enabled the camera, which the randomizer must never do unprompted");
        }
    }
}
