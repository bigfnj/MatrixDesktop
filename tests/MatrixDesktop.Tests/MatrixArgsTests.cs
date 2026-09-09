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
}
