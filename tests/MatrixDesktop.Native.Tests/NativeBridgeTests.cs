using System.Collections.Concurrent;
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
        ("Native bridge refresh never re-reads a UC3D desktop", RefreshKeepsUc3dDesktop),
        ("Native bridge is refreshable only for ZMatrix", OnlyZMatrixIsRefreshable),
        ("Native bridge bootstrap marks only wallpaper, /s and /p hosts non-interactive", InteractiveFollowsHostKind),
        ("Native bridge ZMatrix startup prepares once", ZMatrixStartupPreparesOnce),
        ("Native bridge UC3D snapshot precedes show and survives startup", Uc3dSnapshotPrecedesShow),
        ("Native bridge reuses the wallpaper and atlases across a colour-only ZMatrix edit", ColourEditReusesNativeWork),
        ("Native bridge rebuilds only the native work whose input changed", ChangedInputsRebuild),
        ("Native bridge refresh keeps the generation when nothing changed", UnchangedRefreshKeepsGeneration),
        ("Native bridge refresh publishes a changed wallpaper", ChangedWallpaperRefreshPublishes),
        ("Native bridge refresh rebuilds the atlases when font smoothing changes", SmoothingRefreshRebuildsAtlases),
        ("Native bridge builds edit and refresh work off the UI thread", NativeWorkRunsOffTheUiThread),
        ("Native bridge keeps atlas roles apart when their inputs match", BuiltAtlasRolesStayApart),
        ("Native bridge same-query update follows a wallpaper change", SameQueryUpdateFollowsWallpaper),
        ("Native bridge keeps native work off the UI thread when the environment changes mid-build", MidBuildChangesStayOffTheUiThread),
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

    // A stand-in for the WinForms UI thread: continuations posted here run on the thread that
    // pumps it, the test thread. Every asynchronous bridge call goes through it, because the
    // HostLifecycleTests controls leave a WindowsFormsSynchronizationContext on this thread that
    // nothing pumps, and blocking under that context would never resume a pool continuation.
    private sealed class UiThread : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        internal static T Run<T>(Func<Task<T>> start)
        {
            var context = new UiThread();
            var previous = Current;
            SetSynchronizationContext(context);
            try
            {
                var task = start();
                task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
                foreach (var (callback, state) in context._queue.GetConsumingEnumerable()) callback(state);
                return task.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); }
        }
    }

    private static bool Refresh(NativeMediaBridge bridge, string query) => UiThread.Run(() => bridge.RefreshAsync(query, Virtual, 96));

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

    // Production refreshes only a Refreshable (ZMatrix) bridge, and the UC3D desktop is a still
    // snapshot, so a refresh that does reach a UC3D bridge reads nothing again and publishes nothing.
    private static void RefreshKeepsUc3dDesktop()
    {
        const string query = "scene=uc3d&desktopBackground=true";
        foreach (var (name, kind) in new[] { ("wallpaper", NativeHostKind.Wallpaper), ("interactive", NativeHostKind.Interactive) })
        {
            var captures = new Captures();
            using var bridge = Bridge(kind, captures);
            bridge.Prepare(query, Virtual);
            var before = bridge.BootstrapJson();
            var published = Refresh(bridge, query);
            Require(captures.Wallpaper + captures.Screen == 1 && !published && bridge.BootstrapJson() == before,
                $"A refresh must not re-read the {name} host's UC3D desktop or republish it: {captures.Wallpaper + captures.Screen} reads, published={published}, bootstrap identical={bridge.BootstrapJson() == before}.");
        }
    }

    // The tray item and the broadcast timer refresh only while the bridge is Refreshable.
    private static void OnlyZMatrixIsRefreshable()
    {
        foreach (var kind in Enum.GetValues<NativeHostKind>())
            foreach (var (scene, refreshable) in new[] { ("zmatrix", true), ("uc3d", false), ("matrix", false) })
            {
                using var bridge = ZMatrixBridge(new Work(), kind);
                bridge.Prepare("scene=" + scene, Small);
                Require(bridge.Refreshable == refreshable, $"A {scene} bridge on a {kind} host must report Refreshable={refreshable}, got {bridge.Refreshable}.");
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
        // The thread each builder and each raster-settings read ran on.
        internal readonly ConcurrentQueue<(string Step, int Thread)> Threads = new();
        internal void Ran(string step) => Threads.Enqueue((step, Environment.CurrentManagedThreadId));
        // A broadcast that lands while a build runs: Race runs inside the next Races builds of
        // RaceStep, each after that build has made its result.
        internal string RaceStep = "";
        internal int Races;
        internal Action? Race;
        internal T Raced<T>(string step, T result)
        {
            if (step == RaceStep && Race is { } race && Interlocked.Decrement(ref Races) >= 0) race();
            return result;
        }
    }

    private static NativeMediaBridge ZMatrixBridge(Work work, NativeHostKind kind = NativeHostKind.Wallpaper) => new(false, kind)
    {
        WallpaperCapture = bounds =>
        {
            work.Ran("wallpaper capture");
            Interlocked.Increment(ref work.Wallpaper);
            return work.Raced("wallpaper capture", new WallpaperBackground.Capture([work.Fill, 5, 6], bounds.Width, bounds.Height, bounds.X, bounds.Y, [0, 0, 0]));
        },
        AtlasFactory = (font, repertoire, special, dpi, path, prefix, url) =>
        {
            work.Ran(special ? "special atlas" : "normal atlas");
            if (special) Interlocked.Increment(ref work.Special); else Interlocked.Increment(ref work.Normal);
            var id = prefix + "-0";
            return work.Raced(special ? "special atlas" : "normal atlas", new GdiGlyphAtlas.Atlas(new { pages = new[] { new { url = url(id) } }, font, dpi },
                new Dictionary<string, byte[]> { [id] = Encoding.UTF8.GetBytes($"{work.Raster}|{font}|{string.Concat(repertoire)}") }));
        },
        RasterEnvironment = () => { work.Ran("raster settings read"); return work.Raster; },
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
        var published = Refresh(bridge, "scene=zmatrix");
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
        var published = Refresh(bridge, "scene=zmatrix");
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
        var published = Refresh(bridge, "scene=zmatrix");
        Require(work.Atlases == 4 && published && Generation(bridge) == 2,
            $"A font-smoothing change must rebuild and publish both atlases: atlases={work.Atlases}, published={published}, generation={Generation(bridge)}.");
        var fonts = Served(bridge).Where(pair => pair.Key.Contains("/font-", StringComparison.Ordinal)).ToArray();
        Require(fonts.Length == 2 && fonts.All(pair => pair.Value is not null && Encoding.UTF8.GetString(pair.Value).StartsWith("smoothing-b|", StringComparison.Ordinal)),
            "The published atlases must be the rasters built under the new smoothing settings.");
    }

    // AUD-09: an edit and a refresh build the wallpaper and both atlases on the thread pool and
    // apply them on the UI thread, which only reads the raster settings to key that work.
    private static void NativeWorkRunsOffTheUiThread()
    {
        var ui = Environment.CurrentManagedThreadId;
        var work = new Work();
        using var bridge = ZMatrixBridge(work);
        var font = "&zmFont=" + Uri.EscapeDataString("{\"height\":-12}");
        foreach (var (name, step) in new (string, Action)[]
        {
            ("The first edit", () => Update(bridge, "scene=zmatrix")),
            ("A regular-font edit", () => Update(bridge, "scene=zmatrix" + font)),
            ("A refresh after a wallpaper change", () => { work.Fill = 9; Refresh(bridge, "scene=zmatrix" + font); }),
        })
        {
            var start = work.Threads.Count;
            step();
            var builds = work.Threads.Skip(start).Where(entry => entry.Step != "raster settings read").ToArray();
            var inline = builds.Where(entry => entry.Thread == ui).Select(entry => entry.Step).ToArray();
            Require(builds.Length > 0 && inline.Length == 0, $"{name} must build its native work off the UI thread: {builds.Length} builds, on it [{string.Join(", ", inline)}].");
        }
        var reads = work.Threads.Where(entry => entry.Step == "raster settings read").ToArray();
        Require(reads.Length > 0 && reads.All(entry => entry.Thread == ui),
            $"Keys are read and results applied on the UI thread: {reads.Count(entry => entry.Thread != ui)} of {reads.Length} raster settings reads ran elsewhere.");
        // Built ahead of its Prepare, the regular atlas still carries the generation that built it.
        var normal = Bootstrap(bridge).GetProperty("zmatrixFonts").GetProperty("normal").GetProperty("pages")[0].GetProperty("url").GetString()!;
        Require(normal.Contains("/font-2-normal", StringComparison.Ordinal), $"The edited regular atlas must carry generation 2 in its ids: {normal}.");
    }

    private static void Update(NativeMediaBridge bridge, string query) =>
        UiThread.Run(async () => { await bridge.UpdateAsync(query, Virtual); return true; });

    // A regular repertoire equal to the default phrase's characters gives both roles the same
    // key, but GDI still rasterises the special role with its own default face.
    private static void BuiltAtlasRolesStayApart()
    {
        var work = new Work();
        using var bridge = ZMatrixBridge(work);
        var characters = JsonSerializer.Serialize("The matrix has you".Select(c => c.ToString()).Distinct().ToArray());
        Update(bridge, "scene=zmatrix&zmCharacters=" + Uri.EscapeDataString(characters));
        var bootstrap = Bootstrap(bridge);
        var fonts = bootstrap.GetProperty("zmatrixFonts");
        var special = fonts.ValueKind == JsonValueKind.Object ? fonts.GetProperty("special").GetProperty("pages")[0].GetProperty("url").GetString()!
            : $"none published (warnings: {bootstrap.GetProperty("warnings")})";
        Require(work.Normal == 1 && work.Special == 1 && special.Contains("-special", StringComparison.Ordinal),
            $"Each role must publish its own atlas: normal={work.Normal}, special={work.Special}, special page {special}.");
    }

    // R2-09: the configurator re-sends the same query when its preview is shown again, so a
    // wallpaper change in between must reach that update; one that changed nothing republishes nothing.
    private static void SameQueryUpdateFollowsWallpaper()
    {
        var work = new Work();
        using var bridge = ZMatrixBridge(work);
        string Url() => Bootstrap(bridge).GetProperty("wallpaper").GetProperty("url").GetString()!;
        void Broadcast() => bridge.OnUserPreferenceChanged(null, new Microsoft.Win32.UserPreferenceChangedEventArgs(Microsoft.Win32.UserPreferenceCategory.Desktop));
        Update(bridge, "scene=zmatrix");
        var first = Url();
        Broadcast();
        Update(bridge, "scene=zmatrix");
        Require(work.Wallpaper == 2 && Url() == first && Generation(bridge) == 1,
            $"A same-query update after a broadcast must re-read the wallpaper, and an unchanged one keeps its id and generation 1: {work.Wallpaper} reads, {Url()} at generation {Generation(bridge)}.");
        work.Fill = 9;
        Broadcast();
        Update(bridge, "scene=zmatrix");
        Require(Url() != first && Served(bridge, Url()) is [9, ..] && Generation(bridge) == 2,
            $"A same-query update after a wallpaper change must serve the new wallpaper under a new id as generation 2: {Url()} serves [{string.Join(",", Served(bridge, Url()) ?? [])}] at generation {Generation(bridge)}.");
    }

    // R2-08: a preference, display or smoothing broadcast reaches the UI thread while an edit or a
    // refresh awaits its pool build. Whatever it expired is rebuilt on the pool, never by Prepare.
    private static void MidBuildChangesStayOffTheUiThread()
    {
        var ui = Environment.CurrentManagedThreadId;
        var work = new Work();
        using var bridge = ZMatrixBridge(work);
        void Broadcast() => bridge.OnUserPreferenceChanged(null, new Microsoft.Win32.UserPreferenceChangedEventArgs(Microsoft.Win32.UserPreferenceCategory.Desktop));
        string Wallpaper() => string.Join(",", Served(bridge, Bootstrap(bridge).GetProperty("wallpaper").GetProperty("url").GetString()!) ?? []);
        void Leg(string name, string step, int races, Action race, Action run)
        {
            (work.RaceStep, work.Races, work.Race) = (step, races, race);
            var start = work.Threads.Count;
            run();
            work.Race = null;
            var inline = work.Threads.Skip(start).Where(entry => entry.Step != "raster settings read" && entry.Thread == ui).Select(entry => entry.Step).ToArray();
            Require(inline.Length == 0, $"{name} must keep its native work off the UI thread: on it [{string.Join(", ", inline)}].");
        }
        var query = "scene=zmatrix&zmFont=" + Uri.EscapeDataString("{\"height\":-12}");
        Update(bridge, "scene=zmatrix");
        Leg("A refresh whose wallpaper changes during its pool capture", "wallpaper capture", 1, () => { work.Fill = 7; Broadcast(); }, () => Refresh(bridge, "scene=zmatrix"));
        Require(Wallpaper().StartsWith("7,", StringComparison.Ordinal), $"That refresh must serve the wallpaper captured after the change: [{Wallpaper()}].");
        Leg("A regular-font edit whose wallpaper changes during its pool atlas build", "normal atlas", 1, () => { work.Fill = 8; Broadcast(); }, () => Update(bridge, query));
        Require(Wallpaper().StartsWith("8,", StringComparison.Ordinal), $"That edit must serve the wallpaper captured after the change: [{Wallpaper()}].");
        Leg("A refresh whose font smoothing changes during its pool capture", "wallpaper capture", 1, () => work.Raster = "smoothing-b", () => Refresh(bridge, query));
        var fonts = Served(bridge).Where(pair => pair.Key.Contains("/font-", StringComparison.Ordinal)).Select(pair => pair.Value is null ? "" : Encoding.UTF8.GetString(pair.Value)).ToArray();
        Require(fonts.Length == 2 && fonts.All(text => text.StartsWith("smoothing-b|", StringComparison.Ordinal)),
            $"That refresh must serve atlases built under the new smoothing: [{string.Join(", ", fonts)}].");
        // Inputs that move on every pass: three passes, then the last one applies under its own epoch.
        var captures = work.Wallpaper;
        var published = false;
        Leg("A refresh whose wallpaper changes during every pool capture", "wallpaper capture", 5, () => { work.Fill++; Broadcast(); }, () => published = Refresh(bridge, query));
        Require(work.Wallpaper - captures == 3 && published && Wallpaper().StartsWith($"{work.Fill - 1},", StringComparison.Ordinal),
            $"That refresh must stop after three pool captures and publish the last: {work.Wallpaper - captures} captures, published={published}, serves [{Wallpaper()}] with fill {work.Fill} current.");
        Leg("The same-query update after it", "", 0, () => { }, () => Update(bridge, query));
        Require(Wallpaper().StartsWith($"{work.Fill},", StringComparison.Ordinal), $"The next update must catch up with the wallpaper that changed after the last pass: [{Wallpaper()}], fill {work.Fill}.");
    }
}
