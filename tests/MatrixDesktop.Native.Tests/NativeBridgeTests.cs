using System.Drawing;
using System.Text.Json;
using MatrixDesktop.Shared;

// The real NativeMediaBridge, driven without a WebView: Prepare plus the bootstrap
// JSON the page would receive.
internal static class NativeBridgeTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Native bridge replaces an invalid ZMatrix phrase list with the page's default", InvalidPhrasesUseDefault),
        ("Native bridge replaces an invalid ZMatrix character list with the page's default", InvalidCharactersUseDefault),
    ];

    private static readonly Rectangle Small = new(0, 0, 32, 32);

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static JsonElement Bootstrap(NativeMediaBridge bridge) => JsonDocument.Parse(bridge.BootstrapJson()).RootElement.Clone();

    private static HashSet<string> Glyphs(JsonElement bootstrap, string role)
    {
        var fonts = bootstrap.GetProperty("zmatrixFonts");
        Require(fonts.ValueKind == JsonValueKind.Object, $"The bridge must publish native ZMatrix fonts; warnings: {bootstrap.GetProperty("warnings")}");
        return fonts.GetProperty(role).GetProperty("glyphs").EnumerateArray().Select(glyph => glyph.GetProperty("text").GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    private static void InvalidPhrasesUseDefault()
    {
        using var bridge = new NativeMediaBridge(false);
        bridge.Prepare("scene=zmatrix&zmPhrases=" + Uri.EscapeDataString("[\"Hi\\u0007\"]"), Small);
        var special = Glyphs(Bootstrap(bridge), "special");
        var expected = "The matrix has you".Select(c => c.ToString()).ToHashSet(StringComparer.Ordinal);
        Require(special.SetEquals(expected), $"A phrase list with a control character must fall back to the default phrase, as config.js does; got [{string.Join(",", special)}].");
        // Control: a valid phrase list still reaches the atlas unchanged.
        bridge.Prepare("scene=zmatrix&zmPhrases=" + Uri.EscapeDataString("[\"Hi\"]"), Small);
        Require(Glyphs(Bootstrap(bridge), "special").SetEquals(["H", "i"]), "A valid phrase list must still define the special atlas.");
    }

    private static void InvalidCharactersUseDefault()
    {
        using var bridge = new NativeMediaBridge(false);
        bridge.Prepare("scene=zmatrix&zmCharacters=" + Uri.EscapeDataString("[\"A\",\"BC\"]"), Small);
        var normal = Glyphs(Bootstrap(bridge), "normal");
        Require(normal.SetEquals("1234567890ABCDEFGHIJKLMNOPQRSUTVWXYZabcd".Select(c => c.ToString())),
            $"A character entry longer than one character must fall back to the default repertoire; got [{string.Join(",", normal)}].");
        bridge.Prepare("scene=zmatrix&zmCharacters=" + Uri.EscapeDataString("[\"A\",\"B\"]"), Small);
        Require(Glyphs(Bootstrap(bridge), "normal").SetEquals(["A", "B"]), "A valid character list must still define the regular atlas.");
    }
}
