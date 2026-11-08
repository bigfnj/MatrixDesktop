using MatrixDesktopConfigurator;

namespace MatrixDesktop.Tests;

internal static class MatrixArgsTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("No arguments produce an empty query string", NoArgsIsEmpty),
        ("A key equals value pair becomes a query parameter", PairBecomesParameter),
        ("A GNU style double dash prefix is stripped", GnuPrefixStripped),
        ("A Windows style slash prefix is stripped", SlashPrefixStripped),
        ("A space separated key and value are joined", SpaceSeparatedJoined),
        ("Explicit flag values keep literal ampersands and percent signs", PrefixedLiteralValues),
        ("Standalone structured pairs keep JSON while bare queries still split", StructuredInlineValues),
        ("A negative numeric value is not mistaken for a flag", NegativeNumberIsAValue),
        ("The last value wins when a key repeats", LastValueWins),
        ("A bare boolean flag is treated as true", BareBooleanIsTrue),
        ("Boolean aliases yes, on and 1 normalise to true", BooleanTruthyAliases),
        ("Boolean aliases no, off and 0 normalise to false", BooleanFalsyAliases),
        ("An unknown key with no value is dropped rather than guessed", UnknownKeyWithoutValueDropped),
        ("Help tokens are recognised in every documented spelling", HelpTokensRecognised),
        ("A help token is never forwarded as a query parameter", HelpTokenNotForwarded),
        ("Values are percent encoded on the key equals value path", PairPathEncodes),
        ("A raw query string is percent encoded like any other input", RawQueryPathEncodes),
        ("An already encoded raw query is not encoded twice", RawQueryPathDoesNotDoubleEncode),
        ("Boolean aliases normalise on the raw query path too", RawQueryNormalisesBooleans),
        ("An ampersand joined string without a leading question mark is accepted", BareAmpersandStringAccepted),
        ("Text values containing equals survive the native parser", TextValuesKeepEquals),
        ("The native text-key list matches the catalog's text fields", TextKeysMatchCatalog),
    ];

    private static string Build(params string[] args) => MatrixArgs.BuildQueryString(args);

    private static void NoArgsIsEmpty()
    {
        Check.Equal(string.Empty, Build(), "no arguments means no query string, so index.html loads with defaults");
        Check.Equal(string.Empty, MatrixArgs.BuildQueryString(null), "a null argument list must not throw");
    }

    private static void PairBecomesParameter()
        => Check.Equal("version=3d", Build("version=3d"), "the plain key=value form is the documented default");

    private static void GnuPrefixStripped()
        => Check.Equal("version=3d", Build("--version=3d"), "the guide documents --key=value as equivalent");

    private static void SlashPrefixStripped()
        => Check.Equal("version=3d", Build("/version=3d"), "the guide documents /key=value for cmd users");

    private static void SpaceSeparatedJoined()
        => Check.Equal("effect=mirror", Build("--effect", "mirror"), "--key value is the form used throughout the README");

    private static void NegativeNumberIsAValue()
        => Check.Equal("slant=-12", Build("--slant", "-12"),
            "a leading minus is not a flag prefix, so negative numbers must survive; only -- and / start a key");

    private static void PrefixedLiteralValues()
    {
        const string value = "https://example.test/?left=a%20b&right=2#tail";
        foreach (var prefix in new[] { "--", "/" })
            Check.Equal("url=" + Uri.EscapeDataString(value), Build(prefix + "url=" + value),
                "An explicit flag is literal argv, not an already encoded raw query.");
    }

    private static void StructuredInlineValues()
    {
        foreach (var prefix in new[] { "", "--", "/" })
        foreach (var pair in new[] {
            ("zmPhrases", """["A=B & C","\"quoted\"","漢"]"""),
            ("zmFont", """{"name":"A=B & C","height":-12}""")
        })
            Check.Equal(pair.Item1 + "=" + Uri.EscapeDataString(pair.Item2),
                Build(prefix + pair.Item1 + "=" + pair.Item2),
                "Valid structured JSON is one literal value in each inline spelling.");
        Check.Equal("items=%5B1%5D&camera=true", Build("items=[1]&camera=yes"),
            "A real bare query following an array value must still split and normalize.");
    }

    private static void LastValueWins()
        => Check.Equal("effect=stripes", Build("--effect", "palette", "--effect", "stripes"),
            "the guide documents last-wins for duplicate keys, which is what RunMatrixDesktop_ARGS.bat relies on");

    private static void BareBooleanIsTrue()
        => Check.Equal("clickRipples=true", Build("--clickRipples"),
            "clickRipples is in BoolKeys, so a bare flag means true");

    private static void BooleanTruthyAliases()
    {
        foreach (var v in new[] { "yes", "on", "1", "y", "true" })
        {
            Check.Equal("camera=true", Build("--camera", v), $"'{v}' is a documented truthy alias");
        }
    }

    private static void BooleanFalsyAliases()
    {
        foreach (var v in new[] { "no", "off", "0", "n", "false" })
        {
            Check.Equal("camera=false", Build("--camera", v), $"'{v}' is a documented falsy alias");
        }
    }

    private static void UnknownKeyWithoutValueDropped()
        => Check.Equal(string.Empty, Build("--notARealFlag"),
            "a non-boolean key with no value is a typo; forwarding it would put junk in the query string");

    private static void HelpTokensRecognised()
    {
        foreach (var token in new[] { "--help", "-h", "/?", "help", "HELP", "--HELP" })
        {
            Check.True(MatrixArgs.IsHelpRequested([token]), $"'{token}' is documented as requesting help");
        }
        Check.False(MatrixArgs.IsHelpRequested(["--helpful"]), "only exact help tokens count, not prefixes");
        Check.False(MatrixArgs.IsHelpRequested([]), "an empty argument list is not a help request");
    }

    private static void HelpTokenNotForwarded()
        => Check.Equal("version=3d", Build("--help", "version=3d"),
            "a help token must never reach the web layer as a query parameter");

    private static void PairPathEncodes()
        => Check.Equal("url=a%20b", Build("--url", "a b"),
            "the key=value path percent encodes, so a space cannot corrupt the URL");

    private static void RawQueryPathEncodes()
    {
        // MD-17 fixed. Both branches now encode, so a space cannot produce an invalid URL
        // and a '#' cannot silently truncate every parameter after it by starting a
        // fragment.
        Check.Equal("a=b%20c&d=e", Build("?a=b c&d=e"),
            "the raw query branch must encode exactly like the key=value branch");
        Check.Contains(Build("?url=a#b&effect=stripes"), "effect=stripes",
            "a '#' must be encoded rather than swallowing every later parameter as a fragment");
    }

    private static void RawQueryPathDoesNotDoubleEncode()
    {
        // A pasted query is normally already encoded, so the value is decoded before being
        // re-encoded. Without the decode step '%20' would become '%2520'.
        Check.Equal("url=a%20b", Build("?url=a%20b"),
            "an already encoded value must survive the round trip unchanged");
    }

    private static void RawQueryNormalisesBooleans()
        => Check.Equal("camera=true", Build("?camera=yes"),
            "boolean aliases are normalised on the raw query path too, not only on the pair path");

    private static void BareAmpersandStringAccepted()
        => Check.Equal("a=b&c=d", Build("a=b&c=d"),
            "people paste query strings without the leading question mark, which the parser accepts");

    // AUD-75. A following token containing '=' used to start the next key, so a text value
    // such as C:\Music\Side A\a=b.mp3 or an image URL query was dropped and its fragment
    // arrived as a junk key. Real drafts travel BuildArguments -> AppCli -> MatrixArgs.
    // Killing mutation: restore the equals-as-next-key rule in MatrixArgs only.
    private static void TextValuesKeepEquals()
    {
        var builder = new CommandBuilder(ArgumentCatalog.Create());
        var uc3d = builder.CreateSceneDefaultDraft("uc3d");
        uc3d["sound"] = "music";
        uc3d["musicFile"] = @"C:\Music\Side A\a=b.mp3";
        uc3d["showPictures"] = true;
        uc3d["pictureSource"] = "custom";
        uc3d["pictureDirectory"] = @"D:\My Pictures\x=y";
        uc3d["donorDirectory"] = @"D:\Donor Media\k=v";
        var image = builder.CreateDefaultDraft();
        image["effect"] = "image";
        image["url"] = "https://example.test/a.png?a=b";
        foreach (var draft in new[] { uc3d, image })
        {
            AppCli.Parse(builder.BuildArguments(draft, false, false).ToArray(), out var passthrough);
            var query = Query(MatrixArgs.BuildQueryString(passthrough));
            var keys = MatrixArgs.TextKeys.Where(key => ((string?)draft[key])?.Length > 0).ToArray();
            Check.True(keys.Length > 0, "each draft must exercise at least one text value");
            foreach (var key in keys)
                Check.Equal((string)draft[key]!, query.GetValueOrDefault(key) ?? "(missing)", key + " must reach the renderer exactly");
            Check.False(query.Keys.Any(key => key.Contains('\\') || key.Contains('/')),
                "no fragment of a value may arrive as a key: " + string.Join(", ", query.Keys));
        }

        // Controls that parse as before: a text flag stops at an explicit flag, and a bare pair
        // is a key after a value-free flag or a complete pair.
        Check.Equal("scene=uc3d", Build("--musicFile", "--scene", "uc3d"), "a text flag without a value is dropped and the next flag survives");
        Check.Equal("camera=true&effect=stripes", Build("--camera", "effect=stripes"), "a bare pair after a boolean flag is still a key");
        Check.Equal("effect=mirror&version=3d", Build("--effect", "mirror", "version=3d"), "a bare pair after a complete pair is still a key");
        Check.Equal("url=" + Uri.EscapeDataString("C:/a=b.png"), Build("--url=C:/a=b.png"), "the inline spelling splits at the first equals only");
    }

    private static void TextKeysMatchCatalog()
        => Check.SetEqual(ArgumentCatalog.Create().Groups.SelectMany(group => group.Fields)
                .Where(field => field.Scope == "web" && field.Kind == "text").Select(field => field.ArgName),
            MatrixArgs.TextKeys,
            "MatrixArgs.TextKeys must name exactly the catalog's text fields, or a new text field would lose an '=' value and its json64 decode");

    private static Dictionary<string, string> Query(string query)
        => query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair.Length > 1 ? pair[1] : string.Empty));
}
