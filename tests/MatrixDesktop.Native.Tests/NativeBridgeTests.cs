using System.Drawing;
using System.Text;
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
        ("Native bridge ZMatrix startup prepares once", ZMatrixStartupPreparesOnce),
        ("Native bridge UC3D snapshot precedes show and survives startup", Uc3dSnapshotPrecedesShow),
        ("Native bridge reuses the wallpaper and atlases across a colour-only ZMatrix edit", ColourEditReusesNativeWork),
        ("Native bridge rebuilds only the native work whose input changed", ChangedInputsRebuild),
        ("Native bridge refresh keeps the generation when nothing changed", UnchangedRefreshKeepsGeneration),
        ("Native bridge refresh publishes a changed wallpaper", ChangedWallpaperRefreshPublishes),
        ("Native bridge refresh rebuilds the atlases when font smoothing changes", SmoothingRefreshRebuildsAtlases),
    ];

    // MainForm's startup order: the constructor runs the before-show step, then
    // InitializeCurrentWebViewAsync sets the staged web root and prepares.
    private static readonly string StagedRoot = Path.Combine(AppContext.BaseDirectory, "staged-web-root");

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

    private static void ZMatrixStartupPreparesOnce()
    {
        const string query = "scene=zmatrix";
        var captures = new Captures();
        using var bridge = Bridge(NativeHostKind.Wallpaper, captures);
        bridge.CaptureDesktopBeforeShow(query, Virtual);
        bridge.SetAssetRoot(StagedRoot);
        bridge.Prepare(query, Virtual, 96);
        var bootstrap = Bootstrap(bridge);
        var generation = bootstrap.GetProperty("nativeGeneration").GetInt32();
        Require(captures.Wallpaper == 1 && generation == 1,
            $"ZMatrix startup must capture the wallpaper once and publish generation 1: captures={captures.Wallpaper}, generation={generation}.");
        Require(bootstrap.GetProperty("wallpaper").ValueKind == JsonValueKind.Object, "ZMatrix startup must still publish its wallpaper.");
    }

    private static void Uc3dSnapshotPrecedesShow()
    {
        const string query = "scene=uc3d&desktopBackground=true";
        var captures = new Captures();
        using var bridge = Bridge(NativeHostKind.Interactive, captures);
        bridge.CaptureDesktopBeforeShow(query, Virtual);
        Require(captures.Screen == 1, $"The UC3D screen snapshot must be taken before the window is shown: {captures.Screen} captures.");
        bridge.SetAssetRoot(StagedRoot);
        bridge.Prepare(query, Virtual, 96);
        Require(captures.Screen == 1 && Bootstrap(bridge).GetProperty("desktopUrl").ValueKind == JsonValueKind.String,
            $"The before-show snapshot must survive startup with no second capture: {captures.Screen} captures.");
    }

    // Counting stand-ins for the ZMatrix native work. The atlas bytes follow the injected
    // font-smoothing state, as a DEFAULT_QUALITY GDI raster does.
    private sealed class Work
    {
        internal int Wallpaper, Normal, Special;
        internal byte Fill = 4;
        internal string Raster = "smoothing-a";
        internal int Atlases => Normal + Special;
    }

    private static NativeMediaBridge ZMatrixBridge(Work work) => new(false, NativeHostKind.Wallpaper)
    {
        WallpaperCapture = bounds =>
        {
            work.Wallpaper++;
            return new WallpaperBackground.Capture([work.Fill, 5, 6], bounds.Width, bounds.Height, bounds.X, bounds.Y, [0, 0, 0]);
        },
        AtlasFactory = (font, repertoire, special, dpi, path, prefix, url) =>
        {
            if (special) work.Special++; else work.Normal++;
            var id = prefix + "-0";
            return new GdiGlyphAtlas.Atlas(new { pages = new[] { new { url = url(id) } }, font, dpi },
                new Dictionary<string, byte[]> { [id] = Encoding.UTF8.GetBytes($"{work.Raster}|{font}|{string.Concat(repertoire)}") });
        },
        RasterEnvironment = () => work.Raster,
    };

    private static int Generation(NativeMediaBridge bridge) => Bootstrap(bridge).GetProperty("nativeGeneration").GetInt32();

    private static byte[]? Served(NativeMediaBridge bridge, string url) => bridge.Resource(url[(url.LastIndexOf('/') + 1)..]);

    // Every native resource url the bootstrap publishes, with the array served for it.
    private static Dictionary<string, byte[]?> Served(NativeMediaBridge bridge)
    {
        var bootstrap = Bootstrap(bridge);
        var urls = new List<string>();
        if (bootstrap.GetProperty("wallpaper").ValueKind == JsonValueKind.Object) urls.Add(bootstrap.GetProperty("wallpaper").GetProperty("url").GetString()!);
        foreach (var role in new[] { "normal", "special" })
            foreach (var page in bootstrap.GetProperty("zmatrixFonts").GetProperty(role).GetProperty("pages").EnumerateArray())
                urls.Add(page.GetProperty("url").GetString()!);
        return urls.ToDictionary(url => url, url => Served(bridge, url));
    }

    private static void ColourEditReusesNativeWork()
    {
        var work = new Work();
        using var bridge = ZMatrixBridge(work);
        bridge.Prepare("scene=zmatrix&zmRedGain=0.5", Virtual);
        var first = Served(bridge);
        bridge.Prepare("scene=zmatrix&zmRedGain=0.6", Virtual);
        var second = Served(bridge);
        Require(work.Wallpaper == 1 && work.Atlases == 2, $"A colour-only edit must reuse the wallpaper and both atlases: wallpaper={work.Wallpaper}, atlases={work.Atlases}.");
        Require(first.Count == 3 && second.Count == first.Count && second.All(pair => pair.Value is not null && first.TryGetValue(pair.Key, out var bytes) && ReferenceEquals(pair.Value, bytes)),
            "The edited generation must serve the same wallpaper and atlas arrays under the same ids.");
        var bootstrap = Bootstrap(bridge);
        var generation = bootstrap.GetProperty("nativeGeneration").GetInt32();
        var wallpaper = bootstrap.GetProperty("wallpaper").GetProperty("generation").GetInt32();
        var fonts = bootstrap.GetProperty("zmatrixFonts").GetProperty("generation").GetInt32();
        Require(generation == 2 && wallpaper == generation && fonts == generation,
            $"Reused resources must be published under the current generation: native={generation}, wallpaper={wallpaper}, fonts={fonts}.");
    }

    private static void ChangedInputsRebuild()
    {
        var font = "&zmFont=" + Uri.EscapeDataString("{\"height\":-12}");
        var work = new Work();
        using (var bridge = ZMatrixBridge(work))
        {
            bridge.Prepare("scene=zmatrix", Virtual);
            bridge.Prepare("scene=zmatrix" + font, Virtual);
            Require(work.Normal == 2 && work.Special == 1, $"A regular-font edit must rebuild only the regular atlas: normal={work.Normal}, special={work.Special}.");
            var reads = work.Wallpaper;
            bridge.Prepare("scene=zmatrix" + font, Small);
            Require(work.Wallpaper == reads + 1, $"A bounds change must re-read the wallpaper once: {work.Wallpaper - reads} reads.");
        }
        var colour = new Work();
        using (var bridge = ZMatrixBridge(colour))
        {
            bridge.Prepare("scene=zmatrix&zmBackgroundMode=Color", Virtual);
            Require(colour.Wallpaper == 0 && Bootstrap(bridge).GetProperty("wallpaper").ValueKind == JsonValueKind.Null,
                $"Colour mode must not capture a wallpaper the page never decodes: {colour.Wallpaper} reads.");
        }
    }

    private static void UnchangedRefreshKeepsGeneration()
    {
        var work = new Work();
        using var bridge = ZMatrixBridge(work);
        bridge.Prepare("scene=zmatrix", Virtual);
        var published = bridge.RefreshAsync("scene=zmatrix", Virtual, 96).GetAwaiter().GetResult();
        Require(work.Wallpaper == 2, $"A refresh must re-read the wallpaper: {work.Wallpaper} reads.");
        Require(!published && Generation(bridge) == 1,
            $"A refresh that changes nothing must keep generation 1 and not publish, or the page discards its raster: published={published}, generation={Generation(bridge)}.");
    }

    private static void ChangedWallpaperRefreshPublishes()
    {
        var work = new Work();
        using var bridge = ZMatrixBridge(work);
        bridge.Prepare("scene=zmatrix", Virtual);
        var before = Bootstrap(bridge).GetProperty("wallpaper").GetProperty("url").GetString()!;
        work.Fill = 9;
        var published = bridge.RefreshAsync("scene=zmatrix", Virtual, 96).GetAwaiter().GetResult();
        var after = Bootstrap(bridge).GetProperty("wallpaper").GetProperty("url").GetString()!;
        Require(published && Generation(bridge) == 2, $"A changed wallpaper must be published as generation 2: published={published}, generation={Generation(bridge)}.");
        Require(after != before && Served(bridge, after) is [9, ..], "The changed wallpaper must be served under a new id.");
    }

    private static void SmoothingRefreshRebuildsAtlases()
    {
        var work = new Work();
        using var bridge = ZMatrixBridge(work);
        bridge.Prepare("scene=zmatrix", Virtual);
        work.Raster = "smoothing-b";
        var published = bridge.RefreshAsync("scene=zmatrix", Virtual, 96).GetAwaiter().GetResult();
        Require(work.Atlases == 4 && published && Generation(bridge) == 2,
            $"A font-smoothing change must rebuild and publish both atlases: atlases={work.Atlases}, published={published}, generation={Generation(bridge)}.");
        var fonts = Served(bridge).Where(pair => pair.Key.Contains("/font-", StringComparison.Ordinal)).ToArray();
        Require(fonts.Length == 2 && fonts.All(pair => pair.Value is not null && Encoding.UTF8.GetString(pair.Value).StartsWith("smoothing-b|", StringComparison.Ordinal)),
            "The published atlases must be the rasters built under the new smoothing settings.");
    }
}
