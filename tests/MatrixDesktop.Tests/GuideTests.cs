using System.Globalization;
using System.Text.RegularExpressions;
using MatrixDesktopConfigurator;

namespace MatrixDesktop.Tests;

// Holds the three places a flag is defined to each other:
//
//   ArgumentCatalog.cs            what the configurator offers and what CommandBuilder
//                                 treats as the default
//   web/js/config.js paramMapping what the renderer actually accepts
//   MatrixDesktop_Argument_Guide  what the user is told, embedded in both EXEs and shown
//                                 by --help-full and the configurator's ? button
//
// The repo's own BACKLOG proposed generating the guide's flag reference from the catalogue.
// That is not what this does, deliberately. The guide is 660 lines of hand-written prose:
// per-flag meaning, performance notes, recommended ranges, worked examples. The catalogue
// holds an id, a default, a min/max and a one-line help string. Generating from it would
// replace a good document with a worse one. Cross-checking gets the same outcome, drift
// becomes impossible, and the prose survives.
//
// This is not hypothetical tidiness. Every one of these assertions corresponds to a defect
// that actually shipped: cursorColor and the whole palette drifted between the catalogue and
// config.js; glyphIntensity was documented and accepted while no shader read it; the guide's
// own header version went stale twice.
internal static class GuideTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("Every configurator web flag is a parameter the renderer accepts", CatalogMatchesConfigJs),
        ("Every configurator web flag is documented in the argument guide", CatalogIsDocumented),
        ("Every flag the guide documents really exists", GuideDocumentsNothingFictional),
        ("Defaults stated in the guide match the catalogue", GuideDefaultsMatchCatalog),
        ("Every wrapper flag the app accepts is documented in the guide", AppFlagsAreDocumented),
        ("The guide's boolean rules match both parsers", BooleanRulesMatchBothParsers),
    ];

    private static string Resource(string name)
    {
        using var stream = typeof(GuideTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"{name} is not embedded. The EmbeddedResource item in MatrixDesktop.Tests.csproj was " +
                "removed or renamed, which would silently reduce these tests to no-ops.");
        return new StreamReader(stream).ReadToEnd();
    }

    private static string Guide => Resource("Guide.txt");
    private static string ConfigJs => Resource("config.js.txt");

    // Property names inside `const paramMapping = { ... }`, plus the aliases assigned to it
    // afterwards as `paramMapping.foo = paramMapping.bar;`.
    private static HashSet<string> ConfigJsParameters()
    {
        var src = ConfigJs;
        var start = src.IndexOf("const paramMapping = {", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("could not find 'const paramMapping = {' in config.js");
        }

        var depth = 0;
        var end = -1;
        for (var i = start; i < src.Length; i++)
        {
            if (src[i] == '{') { depth++; }
            else if (src[i] == '}')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
        }
        if (end < 0)
        {
            throw new InvalidOperationException("paramMapping block is not brace-balanced");
        }

        var block = src[start..end];
        var names = new HashSet<string>(StringComparer.Ordinal);

        // A property name at exactly one tab of indentation, so nested object literals inside
        // a parser do not leak in as if they were parameters.
        foreach (Match m in Regex.Matches(block, @"^\t(\w+):", RegexOptions.Multiline))
        {
            names.Add(m.Groups[1].Value);
        }

        foreach (Match m in Regex.Matches(src, @"^paramMapping\.(\w+) = paramMapping\.", RegexOptions.Multiline))
        {
            names.Add(m.Groups[1].Value);
        }

        // ZMatrix parameters are generated from their shared validation spec.
        // Read that bounded declaration only when the actual mapping includes it.
        if (Regex.IsMatch(block, @"^\s*\.\.\.zmatrixParamMapping\s*,?\s*$", RegexOptions.Multiline))
        {
            var settings = Resource("zmatrix-settings.js.txt");
            var specStart = settings.IndexOf("export const zmatrixSettingsSpec = Object.freeze({", StringComparison.Ordinal);
            var specEnd = specStart < 0 ? -1 : settings.IndexOf("\n});", specStart, StringComparison.Ordinal);
            Check.True(specStart >= 0 && specEnd > specStart, "ZMatrix settings spec must be a bounded object declaration");
            foreach (Match match in Regex.Matches(settings[specStart..specEnd], @"\b(zm\w+)\s*:"))
                names.Add(match.Groups[1].Value);
        }

        Check.True(names.Count > 40,
            $"only parsed {names.Count} parameters out of config.js, so the parser no longer matches the " +
            "file and every assertion built on it would pass against almost nothing");

        return names;
    }

    // Each "- Key: name" line in the guide, mapped to the indented block beneath it.
    private static Dictionary<string, string> GuideEntries()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = Guide.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^- Key: (\S+)\s*$");
            if (!m.Success) { continue; }

            var body = new List<string>();
            for (var j = i + 1; j < lines.Length && (lines[j].StartsWith("- ") || lines[j].StartsWith("  ")); j++)
            {
                body.Add(lines[j]);
            }
            entries[m.Groups[1].Value] = string.Join("\n", body);
        }

        Check.True(entries.Count > 30,
            $"only found {entries.Count} '- Key:' entries in the guide, so the parser no longer matches it");

        return entries;
    }

    private static ArgumentDefinition[] WebFields() =>
        ArgumentCatalog.Create().Groups
            .SelectMany(g => g.Fields)
            .Where(f => f.Scope == "web")
            .ToArray();

    private static void CatalogMatchesConfigJs()
    {
        var known = ConfigJsParameters();
        var unknown = WebFields().Select(f => f.ArgName).Where(n => !known.Contains(n)).ToArray();

        Check.Equal(0, unknown.Length,
            "the configurator offers web flags the renderer does not accept, so the generated command " +
            $"would be silently ignored: [{string.Join(", ", unknown)}]");
    }

    private static void CatalogIsDocumented()
    {
        var documented = GuideEntries();
        var missing = WebFields().Select(f => f.ArgName).Where(n => !documented.ContainsKey(n)).ToArray();

        Check.Equal(0, missing.Length,
            "these web flags are offered by the configurator but appear nowhere in the argument guide, " +
            $"which is the reference users are pointed at: [{string.Join(", ", missing)}]");
    }

    private static void GuideDocumentsNothingFictional()
    {
        var known = ConfigJsParameters();
        var fictional = GuideEntries().Keys.Where(k => !known.Contains(k)).ToArray();

        Check.Equal(0, fictional.Length,
            "the guide documents flags that config.js does not map, so passing them does nothing. " +
            $"A reference that lies costs more trust than its own size: [{string.Join(", ", fictional)}]");
    }

    private static void GuideDefaultsMatchCatalog()
    {
        var entries = GuideEntries();
        var mismatches = new List<string>();
        var compared = 0;

        foreach (var field in WebFields())
        {
            if (!entries.TryGetValue(field.ArgName, out var body)) { continue; }

            var stated = Regex.Match(body, @"^- Default: (.+)$", RegexOptions.Multiline);
            if (!stated.Success) { continue; }

            // Guide defaults carry qualifiers like "0.03 (varies by version)". Compare the
            // leading token only; the prose after it is the point of hand-writing the guide.
            var text = stated.Groups[1].Value.Trim();
            var token = text.Split(' ')[0].TrimEnd(',', '.');

            var expected = field.DefaultValue switch
            {
                bool b => b ? "true" : "false",
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                int i => i.ToString(CultureInfo.InvariantCulture),
                string s => s,
                _ => null,
            };
            if (expected is null) { continue; }

            compared++;

            // "Unset" is spelled differently on each side and that is not drift. config.js
            // defaults testFix to null, the guide says null, and the catalogue uses "" because
            // a <select> needs a string for its "None" option. CommandBuilder omits a field
            // equal to its default, so nothing is emitted and the renderer falls back to null.
            // Narrow on purpose: only an EMPTY catalogue default may match null or none.
            var bothUnset = expected.Length == 0 &&
                (token.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                 token.Equals("none", StringComparison.OrdinalIgnoreCase));
            if (bothUnset) { continue; }

            // Numeric comparison where both sides parse, so "1" and "1.0" agree.
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
                double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var b2))
            {
                if (Math.Abs(a - b2) > 1e-9)
                {
                    mismatches.Add($"{field.ArgName}: guide says {token}, catalogue says {expected}");
                }
            }
            else if (!string.Equals(token, expected, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add($"{field.ArgName}: guide says '{token}', catalogue says '{expected}'");
            }
        }

        Check.True(compared > 15,
            $"only compared {compared} defaults, so this assertion is not covering the guide any more");
        Check.Equal(0, mismatches.Count,
            $"the guide and the configurator disagree about default values: [{string.Join("; ", mismatches)}]");
    }

    private static void AppFlagsAreDocumented()
    {
        var guide = Guide;
        var undocumented = new List<string>();

        foreach (var field in ArgumentCatalog.Create().Groups.SelectMany(g => g.Fields).Where(f => f.Scope == "app"))
        {
            foreach (var flag in new[] { field.TrueFlag, field.FalseFlag })
            {
                if (string.IsNullOrWhiteSpace(flag)) { continue; }

                // Matched with a trailing boundary so "--topmost" does not count as
                // documenting "--topmost-something".
                if (!Regex.IsMatch(guide, Regex.Escape(flag) + @"(?![\w-])"))
                {
                    undocumented.Add(flag);
                }
            }
        }

        Check.Equal(0, undocumented.Count,
            "the configurator can emit these wrapper flags but the argument guide never mentions them: " +
            $"[{string.Join(", ", undocumented)}]");
    }

    // Section 2's boolean rules, held to the two parsers they describe: the wrapper's
    // FlagNormalization.ParseBool (reached through MatrixArgs.BuildQueryString) and config.js's
    // isTrue. Both parsers changed in 336788d and this section did not, so for a while the guide
    // told users that other values pass through unchanged and that the web app accepts exactly
    // "true", while the wrapper already turned them into false.
    private static void BooleanRulesMatchBothParsers()
    {
        var guide = Guide.Replace("\r\n", "\n");
        var start = guide.IndexOf("\nBooleans\n", StringComparison.Ordinal);
        var end = start < 0 ? -1 : guide.IndexOf("\nComma-separated lists\n", start, StringComparison.Ordinal);
        Check.True(start >= 0 && end > start, "the guide's Booleans section was not found between its own headings");
        var section = guide[start..end];
        // Prose wraps at arbitrary words, so phrases are matched against the section on one line.
        var prose = Regex.Replace(section, @"\s+", " ");

        var isTrue = Regex.Match(ConfigJs, @"const isTrue = \(s\) => \[(?<list>[^\]]*)\]\.includes\(");
        Check.True(isTrue.Success, "config.js no longer declares isTrue as a spelling list, so this test cannot read it");
        var webTrue = Regex.Matches(isTrue.Groups["list"].Value, "\"(?<s>[^\"]*)\"").Select(m => m.Groups["s"].Value).ToArray();
        Check.True(webTrue.Length >= 2, $"read only {webTrue.Length} spellings from isTrue, so comparing them would prove nothing");

        string[] Spellings(string side)
        {
            var line = Regex.Match(section, $@"^\s*{side}\s*<=\s*(?<list>.+)$", RegexOptions.Multiline);
            Check.True(line.Success, $"the guide's '{side} <=' spelling line is missing");
            // "Recommended: true / false" names the canonical spelling on its own line.
            return [.. line.Groups["list"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), side];
        }
        var guideTrue = Spellings("true");
        var guideFalse = Spellings("false");

        Check.SetEqual([.. guideTrue, ""], webTrue,
            "the guide says the web app's own parser accepts the same true spellings as the wrapper plus an empty value");
        foreach (var spelling in guideTrue.Concat(["YES", " on "]))
        {
            Check.True(MatrixDesktop.Shared.FlagNormalization.ParseBool(spelling, defaultWhenMissing: false),
                $"the guide lists '{spelling}' as true, so the wrapper's ParseBool must accept it");
            Check.Equal("camera=true", MatrixDesktop.MatrixArgs.BuildQueryString(["--camera", spelling]),
                $"the guide lists '{spelling}' as true, so it must reach the web app as true");
        }
        foreach (var spelling in guideFalse)
        {
            Check.False(MatrixDesktop.Shared.FlagNormalization.ParseBool(spelling, defaultWhenMissing: true),
                $"the guide lists '{spelling}' as false, so the wrapper's ParseBool must reject it");
            Check.False(webTrue.Contains(spelling, StringComparer.OrdinalIgnoreCase),
                $"the guide lists '{spelling}' as false, so the web app's isTrue must not accept it");
            Check.Equal("camera=false", MatrixDesktop.MatrixArgs.BuildQueryString(["--camera", spelling]),
                $"the guide lists '{spelling}' as false, so it must reach the web app as false");
        }
        Check.Equal("camera=true", MatrixDesktop.MatrixArgs.BuildQueryString(["--camera="]),
            "the guide says an EMPTY value normalizes to true");
        Check.Equal("volumetric=true", MatrixDesktop.MatrixArgs.BuildQueryString(["--volumetric"]),
            "the guide says a bare boolean flag is treated as true");

        // What the guide may say about any other value depends on what the wrapper does with it,
        // so the pass-through wording fails here as soon as it stops being true, and vice versa.
        var passesThrough = MatrixDesktop.MatrixArgs.BuildQueryString(["--camera", "maybe"]) == "camera=maybe";
        var claimsPassThrough = Regex.IsMatch(prose, @"passed through|unchanged|exactly ""true""", RegexOptions.IgnoreCase);
        var claimsConversion = prose.Contains("converted to false by the wrapper", StringComparison.OrdinalIgnoreCase);
        Check.False(claimsPassThrough && !passesThrough,
            "the guide says other values pass through unchanged and the web app accepts exactly \"true\", " +
            "but the wrapper converts --camera maybe to camera=false before the web app sees it");
        Check.False(claimsConversion && passesThrough,
            "the guide says other values are converted to false by the wrapper, but --camera maybe passes through unchanged");
        Check.True(claimsPassThrough || claimsConversion, "the guide must say what happens to any other value");
        foreach (var other in new[] { "maybe", "2" })
        {
            Check.Equal("camera=false", MatrixDesktop.MatrixArgs.BuildQueryString(["--camera", other]),
                $"the guide says --camera {other} is FALSE");
        }

        // The flags these rules cover. Found by behaviour rather than by reflecting on
        // MatrixArgs.BoolKeys: a bare boolean flag becomes key=true, any other bare key is dropped.
        var list = Regex.Match(prose, @"For every boolean web flag \((?<names>[^)]*)\)");
        Check.True(list.Success, "the guide no longer lists the boolean web flags these rules apply to");
        var listed = list.Groups["names"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var booleans = ConfigJsParameters().Where(name => MatrixDesktop.MatrixArgs.BuildQueryString(["--" + name]) == name + "=true").ToArray();
        Check.True(booleans.Length >= 9, $"found only {booleans.Length} boolean web flags, so the list comparison would cover almost nothing");
        var unlisted = booleans.Except(listed, StringComparer.Ordinal).ToArray();
        var notBoolean = listed.Except(booleans, StringComparer.Ordinal).ToArray();
        Check.True(unlisted.Length == 0 && notBoolean.Length == 0,
            $"the guide's boolean flag list disagrees with the flags the wrapper normalizes: unlisted [{string.Join(", ", unlisted)}], " +
            $"listed but not boolean [{string.Join(", ", notBoolean)}]");

        // "for the Matrix and UC3D flags": every listed flag outside ZMatrix is parsed by isTrue.
        var otherParser = listed
            .Where(name => !name.StartsWith("zm", StringComparison.Ordinal))
            .Where(name => !Regex.IsMatch(ConfigJs, $@"^\t{Regex.Escape(name)}: \{{[^\n]*\bparser: isTrue\b", RegexOptions.Multiline))
            .ToArray();
        Check.Equal(0, otherParser.Length,
            $"the guide says the Matrix and UC3D boolean flags use the web app's isTrue, but these do not: [{string.Join(", ", otherParser)}]");
    }
}
