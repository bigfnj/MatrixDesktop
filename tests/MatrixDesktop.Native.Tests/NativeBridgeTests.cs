using System.Drawing;
using System.Text.Json;
using MatrixDesktop;
using MatrixDesktop.Shared;

// The real NativeMediaBridge, driven without a WebView: Prepare plus the bootstrap
// JSON the page would receive.
internal static class NativeBridgeTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Native bridge replaces an invalid ZMatrix phrase list with the page's default", InvalidPhrasesUseDefault),
        ("Native bridge replaces an invalid ZMatrix character list with the page's default", InvalidCharactersUseDefault),
        ("Native bridge takes the UC3D desktop from the wallpaper for wallpaper and preview hosts", DesktopSourceFollowsHostKind),
        ("Native bridge refresh re-reads a wallpaper-sourced UC3D desktop only", RefreshRereadsWallpaperDesktop),
        ("Native bridge bootstrap marks only wallpaper, /s and /p hosts non-interactive", InteractiveFollowsHostKind),
    ];

    private static readonly Rectangle Small = new(0, 0, 32, 32);
    private static readonly Rectangle Virtual = new(-1920, -120, 3840, 1200);

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Captures { internal int Screen, Wallpaper; internal Rectangle Bounds; }

    // Counting stand-ins for the real captures, which would read this desktop's pixels.
    private static NativeMediaBridge Bridge(NativeHostKind kind, Captures captures) => new(false, kind)
    {
        ScreenCapture = bounds => { captures.Screen++; captures.Bounds = bounds; return [1, 2, 3]; },
        WallpaperCapture = bounds =>
        {
            captures.Wallpaper++; captures.Bounds = bounds;
            return new WallpaperBackground.Capture([4, 5, 6], bounds.Width, bounds.Height, bounds.X, bounds.Y, [0, 0, 0]);
        },
    };

    // The host kind MainForm passes for each launch.
    private static NativeHostKind HostKind(HostLaunchKind launch, bool wallpaperWindowMode) =>
        MediaHostKind.For(new HostLaunchPlan([], launch), wallpaperWindowMode);

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

    private static void DesktopSourceFollowsHostKind()
    {
        const string query = "scene=uc3d&desktopBackground=true";
        foreach (var (name, kind, fromWallpaper) in new[]
        {
            ("wallpaper", HostKind(HostLaunchKind.Standard, true), true),
            ("screensaver preview (/p)", HostKind(HostLaunchKind.ScreensaverPreview, false), true),
            ("fullscreen", HostKind(HostLaunchKind.Standard, false), false),
            ("screensaver (/s)", HostKind(HostLaunchKind.Screensaver, false), false),
        })
        {
            var captures = new Captures();
            using var bridge = Bridge(kind, captures);
            bridge.Prepare(query, Virtual);
            Require(captures.Wallpaper == (fromWallpaper ? 1 : 0) && captures.Screen == (fromWallpaper ? 0 : 1),
                $"The {name} host must take its UC3D desktop from the {(fromWallpaper ? "wallpaper" : "screen")}: wallpaper={captures.Wallpaper}, screen={captures.Screen}.");
            Require(captures.Bounds == Virtual, $"The {name} host must capture its own bounds, got {captures.Bounds}.");
            Require(Bootstrap(bridge).GetProperty("desktopUrl").ValueKind == JsonValueKind.String, $"The {name} host must publish its desktop snapshot.");
        }
    }

    private static void RefreshRereadsWallpaperDesktop()
    {
        const string query = "scene=uc3d&desktopBackground=true";
        var wallpaper = new Captures();
        using (var bridge = Bridge(NativeHostKind.Wallpaper, wallpaper))
        {
            bridge.Prepare(query, Virtual);
            bridge.RefreshAsync(query, Virtual, 96).GetAwaiter().GetResult();
            Require(wallpaper.Wallpaper + wallpaper.Screen == 2, $"A wallpaper host's refresh must re-read its desktop: {wallpaper.Wallpaper + wallpaper.Screen} reads.");
        }
        // Control: a screen copy is only clean before this window covers the screen, so it is kept.
        var screen = new Captures();
        using (var bridge = Bridge(NativeHostKind.Interactive, screen))
        {
            bridge.Prepare(query, Virtual);
            bridge.RefreshAsync(query, Virtual, 96).GetAwaiter().GetResult();
            Require(screen.Screen + screen.Wallpaper == 1, $"A refresh must keep a screen snapshot: {screen.Screen + screen.Wallpaper} reads.");
        }
    }

    private static void InteractiveFollowsHostKind()
    {
        // The configurator builds its preview bridge with preview: true and no host kind.
        foreach (var (name, bridge, interactive) in new[]
        {
            ("configurator preview", new NativeMediaBridge(true), true),
            ("fullscreen, windowed or borderless", new NativeMediaBridge(false, HostKind(HostLaunchKind.Standard, false)), true),
            ("wallpaper", new NativeMediaBridge(false, HostKind(HostLaunchKind.Standard, true)), false),
            ("screensaver (/s)", new NativeMediaBridge(false, HostKind(HostLaunchKind.Screensaver, false)), false),
            ("screensaver preview (/p)", new NativeMediaBridge(false, HostKind(HostLaunchKind.ScreensaverPreview, false)), false),
        })
        {
            using (bridge)
            {
                bridge.Prepare("scene=matrix", Small);
                var value = Bootstrap(bridge).GetProperty("interactive");
                Require(value.ValueKind == (interactive ? JsonValueKind.True : JsonValueKind.False),
                    $"The {name} bootstrap must carry interactive={interactive.ToString().ToLowerInvariant()}, got {value}.");
            }
        }
    }
}
