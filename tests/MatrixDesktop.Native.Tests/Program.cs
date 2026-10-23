using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MatrixDesktop.Shared;

var tests = new List<(string Name, Action Test)>
{
    ("Native GDI resolves bundled and special fonts with real metrics", NativeProof.DefaultFaces),
    ("Native GDI height changes the actual raster and metrics", NativeProof.Height),
    ("Native GDI width changes the actual raster and metrics", NativeProof.Width),
    ("Native GDI rotation preserves unclipped ink bounds", NativeProof.Rotation),
    ("Native GDI decoration changes actual glyph pixels", NativeProof.Decoration),
    ("Native GDI quality controls actual coverage", NativeProof.Quality),
    ("Native GDI blank glyphs retain advance without pixels", NativeProof.Space),
    ("Native GDI reports font substitution explicitly", NativeProof.Substitution),
    ("Native GDI rejects excessive character populations", NativeProof.RepertoireLimit),
    ("Native GDI bounds raster work before rendering valid excessive settings", NativeProof.RasterWork),
    ("Native GDI preserves logical metrics across DPI", NativeProof.Dpi),
    ("Native GDI releases every owned GDI handle", NativeProof.Handles),
};
tests.AddRange(HostLifecycleTests.All);
tests.AddRange(AutostartTests.All);
tests.AddRange(ScreensaverFocusTests.All);
tests.AddRange(ForegroundActivationTests.All);
tests.AddRange(CrashDumpTests.All);
if (args.Length == 2 && args[0] == "--only") tests = tests.Where(item => item.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToList();
if (tests.Count == 0) throw new ArgumentException("The native test filter matched no tests.");
var failures = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} native tests passed.");
return failures == 0 ? 0 : 1;

internal static class NativeProof
{
    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "matrix-code.ttf");
    private static GdiGlyphAtlas.Atlas Atlas(object? descriptor = null, string[]? characters = null, bool special = false, int dpi = 96) =>
        GdiGlyphAtlas.Create(descriptor is null ? null : JsonSerializer.Serialize(descriptor), characters ?? ["A", "B", " "], special, dpi, FontPath, "test", id => "https://native-test.invalid/" + id);
    private static JsonElement Metadata(GdiGlyphAtlas.Atlas atlas) => JsonSerializer.SerializeToElement(atlas.Metadata);
    private static double Metric(GdiGlyphAtlas.Atlas atlas, string name) => Metadata(atlas).GetProperty("metrics").GetProperty(name).GetDouble();
    private static string Hash(GdiGlyphAtlas.Atlas atlas) => Convert.ToHexString(SHA256.HashData(atlas.Resources.SelectMany(pair => pair.Value).ToArray()));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static void DefaultFaces()
    {
        Require(File.Exists(FontPath), "The real bundled font must be copied into the test output.");
        var normal = Atlas(); var special = Atlas(special: true);
        Require(Metadata(normal).GetProperty("resolvedFace").GetString() == "Matrix Code Font", "The native atlas must use the privately registered bundled face.");
        Require(Metadata(special).GetProperty("resolvedFace").GetString() == "Terminal", "The special atlas must resolve its requested Windows face.");
        Require(Metric(normal, "height") > 0 && Metric(normal, "maxCharWidth") > 0, "Native metrics must be nonempty.");
        Require(normal.Resources.Count > 0 && Metadata(normal).GetProperty("glyphs").GetArrayLength() == 3, "Native source must supply actual pages and all requested glyphs.");
    }
    internal static void Height()
    {
        var small = Atlas(new { height = -7 }); var large = Atlas(new { height = -24 });
        Require(Metric(large, "height") > Metric(small, "height") * 2, "Changing requested height must enlarge measured native text.");
        Require(Hash(small) != Hash(large), "Changing requested height must change actual pixels.");
    }
    internal static void Width()
    {
        var normal = Atlas(new { height = -24 }); var wide = Atlas(new { height = -24, width = 32 });
        Require(Metric(wide, "maxCharWidth") > Metric(normal, "maxCharWidth"), "Changing requested width must change native width.");
        Require(Hash(normal) != Hash(wide), "Changing requested width must change actual pixels.");
    }
    internal static void Rotation()
    {
        var upright = Atlas(new { height = -24 }, ["I"]);
        var rotated = Atlas(new { height = -24, escapement = 900, orientation = 900 }, ["I"]);
        var a = Metadata(upright).GetProperty("glyphs")[0]; var b = Metadata(rotated).GetProperty("glyphs")[0];
        Require(a.GetProperty("height").GetInt32() > a.GetProperty("width").GetInt32(), "The reference I must be taller than wide.");
        Require(b.GetProperty("width").GetInt32() > b.GetProperty("height").GetInt32(), "The rotated I must be wider than tall.");
        foreach (var glyph in Metadata(rotated).GetProperty("glyphs").EnumerateArray())
        {
            var page = Metadata(rotated).GetProperty("pages")[glyph.GetProperty("page").GetInt32()];
            Require(glyph.GetProperty("x").GetInt32() > 0 && glyph.GetProperty("y").GetInt32() > 0, "Rotated ink needs left/top padding.");
            Require(glyph.GetProperty("x").GetInt32() + glyph.GetProperty("width").GetInt32() < page.GetProperty("width").GetInt32(), "Rotated ink needs right padding.");
        }
    }
    internal static void Decoration()
    {
        var plain = Atlas(new { height = -24 }, ["A"]);
        Require(Hash(plain) != Hash(Atlas(new { height = -24, underline = true }, ["A"])), "Underline must affect native pixels.");
        Require(Hash(plain) != Hash(Atlas(new { height = -24, strikeOut = true }, ["A"])), "Strikeout must affect native pixels.");
        Require(Hash(plain) != Hash(Atlas(new { height = -24, italic = true }, ["A"])), "Italic must affect native pixels.");
    }
    internal static void Quality()
    {
        var aliased = Atlas(new { height = -24, quality = 3 }, ["A"]);
        var antialiased = Atlas(new { height = -24, quality = 4 }, ["A"]);
        Require(Hash(aliased) != Hash(antialiased), "Native quality selection must affect the coverage pixels.");
    }
    internal static void Space()
    {
        var glyph = Metadata(Atlas(characters: [" "])).GetProperty("glyphs")[0];
        Require(glyph.GetProperty("width").GetInt32() == 0 && glyph.GetProperty("height").GetInt32() == 0, "A space must not acquire synthetic visible ink.");
        Require(glyph.GetProperty("advanceX").GetDouble() > 0, "A space must retain a real native advance.");
    }
    internal static void Substitution()
    {
        var metadata = Metadata(Atlas(new { name = "MissingSyntheticFont-725104" }));
        Require(metadata.GetProperty("warnings").GetArrayLength() > 0, "Font substitution must produce a warning.");
        Require(metadata.GetProperty("resolvedFace").GetString() != "MissingSyntheticFont-725104", "The reported face must be the real selected face.");
    }
    internal static void RepertoireLimit()
    {
        try { Atlas(characters: Enumerable.Range(0x100, 513).Select(value => ((char)value).ToString()).ToArray()); }
        catch (InvalidOperationException error) when (error.Message.Contains("512", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("A 513-character regular repertoire must fail its named 512-character limit.");
    }
    internal static void Dpi()
    {
        var normal = Atlas(new { height = -24 }); var doubled = Atlas(new { height = -24 }, dpi: 192);
        Require(Math.Abs(Metric(normal, "height") - Metric(doubled, "height")) <= 1, "DPI changes raster detail, not logical font height.");
        Require(Metadata(doubled).GetProperty("atlasDpi").GetInt32() == 192 && Hash(normal) != Hash(doubled), "The high-DPI atlas must be genuinely rerasterized.");
    }
    internal static void RasterWork()
    {
        GdiGlyphAtlas.ValidateRasterWork(1024, 64);
        var rejected = false;
        try { GdiGlyphAtlas.ValidateRasterWork(1024, 65); }
        catch (InvalidOperationException error) when (error.Message.Contains("raster work exceeds", StringComparison.Ordinal)) { rejected = true; }
        Require(rejected, "The raster-work guard must reject excess work before the real GDI population test runs.");
        rejected = false;
        try { Atlas(new { height = -256, width = 256 }, Enumerable.Range(33, 64).Select(value => ((char)value).ToString()).ToArray()); }
        catch (InvalidOperationException error) when (error.Message.Contains("raster work exceeds", StringComparison.Ordinal)) { rejected = true; }
        Require(rejected, "Valid large-font settings must be rejected by their real measured GDI raster-work budget.");
    }
    internal static void Handles()
    {
        // Exercise the same workload once before measuring: GDI+ lazily retains
        // a process cache on first use. Repeated cycles must then reach a plateau.
        for (var i = 0; i < 12; i++) Atlas(new { height = -7 - i, orientation = i * 100, escapement = i * 100 });
        var before = GetGuiResources(GetCurrentProcess(), 0);
        var controlDc = CreateCompatibleDC(0);
        try { Require(controlDc != 0 && GetGuiResources(GetCurrentProcess(), 0) > before, "The GDI handle counter must observe an owned positive control."); }
        finally { if (controlDc != 0) Require(DeleteDC(controlDc), "The positive-control device context must be deleted."); GdiFlush(); }
        Require(GetGuiResources(GetCurrentProcess(), 0) == before, "The positive-control device context must restore the starting handle count.");
        for (var cycle = 0; cycle < 3; cycle++)
        {
            for (var i = 0; i < 12; i++) Atlas(new { height = -7 - i, orientation = i * 100, escapement = i * 100 });
            GdiFlush();
            var after = GetGuiResources(GetCurrentProcess(), 0);
            Require(after == before, $"Owned GDI handles must be released: before={before}, cycle={cycle}, after={after}.");
        }
    }
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
