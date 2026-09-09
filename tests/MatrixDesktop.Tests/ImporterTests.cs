using System.Text.Json.Nodes;
using MatrixDesktopConfigurator;

namespace MatrixDesktop.Tests;

internal static class ImporterTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("An empty command is rejected with a usable message", EmptyCommandRejected),
        ("A leading MatrixDesktop.exe is trimmed", ExeTrimmed),
        ("A quoted executable path is trimmed", QuotedExePathTrimmed),
        ("A cmd start line with switches is trimmed", StartLineTrimmed),
        ("A raw query string populates the draft", RawQueryImported),
        ("A URL encoded query value is decoded", QueryValueDecoded),
        ("A plus sign in a query value decodes to a space", PlusDecodesToSpace),
        ("Window mode and monitor index are imported together", MonitorImported),
        ("Numeric values are clamped to the field range", NumericClamped),
        ("A non numeric value for a numeric field is ignored", NonNumericIgnored),
        ("An unrecognised key is reported as ignored", UnknownKeyReported),
        ("An RGB colour triplet is imported", RgbColorImported),
        ("An HSL colour triplet is converted to RGB", HslColorConverted),
        ("A palette needs groups of four and rejects a partial group", PaletteArity),
        ("Stripe colours need groups of three and reject a partial group", StripeArity),
        ("Legacy aliases width, dropLength and angle are mapped", LegacyAliases),
        ("An image URL containing a scheme is not treated as a query string", ImageUrlNotQuery),
        ("Stripe effects are recognised by name", StripeEffectRecognition),
    ];

    private static readonly ConfiguratorMetadata Metadata = ArgumentCatalog.Create();
    private static readonly CommandBuilder Builder = new(Metadata);
    private static ArgumentImporter NewImporter() => new(Metadata);

    private static ImportResult Import(string command)
        => NewImporter().Import(command, Builder.CreateDefaultDraft());

    private static double Num(JsonObject draft, string key)
        => draft[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN;

    private static string Str(JsonObject draft, string key)
        => draft[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    private static void EmptyCommandRejected()
    {
        try
        {
            Import("   ");
            throw new InvalidOperationException("expected an ArgumentException for a blank command");
        }
        catch (ArgumentException ex)
        {
            Check.Contains(ex.Message, "Paste", "the message is shown in the UI, so it must tell the user what to do");
        }
    }

    private static void ExeTrimmed()
    {
        var r = Import("MatrixDesktop.exe --effect stripes");
        Check.Equal("stripes", Str(r.Draft, "effect"), "the executable name is not an argument");
    }

    private static void QuotedExePathTrimmed()
    {
        var r = Import("\"C:\\Program Files\\Matrix\\MatrixDesktop.exe\" --effect palette");
        Check.Equal("palette", Str(r.Draft, "effect"), "a full quoted path must be recognised as the launcher");
    }

    private static void StartLineTrimmed()
    {
        // This is the shape RunMatrixDesktop_ARGS.bat uses.
        var r = Import("start \"\" /min \"%EXE%\" --effect stripes --version 3d");
        Check.Equal("stripes", Str(r.Draft, "effect"), "a cmd start line is a documented paste form");
        Check.Equal("3d", Str(r.Draft, "version"), "arguments after the start switches must all import");
    }

    private static void RawQueryImported()
    {
        var r = Import("?version=3d&effect=plain&fallSpeed=0.4");
        Check.Equal("3d", Str(r.Draft, "version"), "a raw query string is a documented paste form");
        Check.Equal("plain", Str(r.Draft, "effect"), "every pair in the query must import");
        Check.Close(0.4, Num(r.Draft, "fallSpeed"), 1e-9, "numeric query values must parse");
    }

    private static void QueryValueDecoded()
    {
        var r = Import("?url=http%3A%2F%2Fexample.com%2Fa.png");
        Check.Equal("http://example.com/a.png", Str(r.Draft, "url"), "query values arrive percent encoded and must be decoded");
    }

    private static void PlusDecodesToSpace()
    {
        var r = Import("?url=a+b");
        Check.Equal("a b", Str(r.Draft, "url"), "form encoding uses plus for space, which the importer handles");
    }

    private static void MonitorImported()
    {
        var r = Import("--monitor 2");
        Check.Equal("single-monitor", Str(r.Draft, "windowMode"), "a monitor index implies single monitor mode");
        Check.Close(2, Num(r.Draft, "monitor"), 1e-9, "the index itself must be kept");
    }

    private static void NumericClamped()
    {
        var high = Import("--numColumns 99999");
        Check.Close(256, Num(high.Draft, "numColumns"), 1e-9, "the catalog caps columns at 256, so a huge value clamps");
        var low = Import("--numColumns 0");
        Check.Close(1, Num(low.Draft, "numColumns"), 1e-9, "the catalog floors columns at 1");
    }

    private static void NonNumericIgnored()
    {
        var r = Import("--numColumns lots");
        Check.True(r.Ignored.Contains("numColumns", StringComparer.OrdinalIgnoreCase),
            "an unparseable number must be reported rather than silently defaulted");
    }

    private static void UnknownKeyReported()
    {
        var r = Import("--notAThing 5");
        Check.True(r.Ignored.Length > 0, "the UI tells the user how many settings were ignored, so this must be populated");
    }

    private static void RgbColorImported()
    {
        var r = Import("--cursorColor 1,0,0");
        var c = r.Draft["cursorColor"] as JsonObject;
        Check.True(c is not null, "a colour field imports as an object with r, g and b");
        Check.Close(1, (double)c!["r"]!, 1e-9, "red channel");
        Check.Close(0, (double)c["g"]!, 1e-9, "green channel");
        Check.Close(0, (double)c["b"]!, 1e-9, "blue channel");
    }

    private static void HslColorConverted()
    {
        // hue 0, full saturation, half lightness is pure red.
        var r = Import("--cursorHSL 0,1,0.5");
        var c = r.Draft["cursorColor"] as JsonObject;
        Check.True(c is not null, "the HSL alias must map onto the same field as the RGB form");
        Check.Close(1, (double)c!["r"]!, 1e-6, "hue 0 at full saturation is red");
        Check.Close(0, (double)c["g"]!, 1e-6, "green must be zero for pure red");
        Check.Close(0, (double)c["b"]!, 1e-6, "blue must be zero for pure red");
    }

    private static void PaletteArity()
    {
        var good = Import("--palette 0,0,0,0,1,1,1,1");
        Check.Equal(2, (good.Draft["palette"] as JsonArray)!.Count, "eight numbers are two stops of r,g,b,at");
        var bad = Import("--palette 0,0,0,0,1,1,1");
        Check.True(bad.Ignored.Contains("palette", StringComparer.OrdinalIgnoreCase),
            "seven numbers is a partial stop, which must be rejected rather than half applied");
    }

    private static void StripeArity()
    {
        var good = Import("--stripeColors 1,0,0,0,1,0");
        Check.Equal(2, (good.Draft["stripeColors"] as JsonArray)!.Count, "six numbers are two RGB triplets");
        var bad = Import("--stripeColors 1,0,0,0");
        Check.True(bad.Ignored.Contains("stripeColors", StringComparer.OrdinalIgnoreCase),
            "four numbers is a partial triplet, which must be rejected");
    }

    private static void LegacyAliases()
    {
        Check.Close(120, Num(Import("--width 120").Draft, "numColumns"), 1e-9, "width is the documented legacy name for numColumns");
        Check.Close(0.5, Num(Import("--dropLength 0.5").Draft, "raindropLength"), 1e-9, "dropLength is the legacy name for raindropLength");
        Check.Close(15, Num(Import("--angle 15").Draft, "slant"), 1e-9, "angle is the legacy name for slant");
    }

    private static void ImageUrlNotQuery()
    {
        var r = Import("--effect image --url https://example.com/a.png?x=1&y=2");
        Check.Contains(Str(r.Draft, "url"), "example.com", "a URL with its own query must not be parsed as MatrixDesktop parameters");
    }

    private static void StripeEffectRecognition()
    {
        foreach (var e in new[] { "stripes", "customStripes", "pride", "trans", "transPride" })
        {
            Check.True(ArgumentImporter.IsStripeEffect(e), $"{e} is documented as stripe based, so stripeColors applies");
        }
        foreach (var e in new[] { "palette", "plain", "mirror", "image", "none", "" })
        {
            Check.False(ArgumentImporter.IsStripeEffect(e), $"{e} cannot use stripeColors, so the field must be gated off");
        }
        Check.False(ArgumentImporter.IsStripeEffect(null), "a null effect must not throw");
    }
}
