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
        ("Native bridge refresh re-reads a wallpaper-sourced UC3D desktop only", RefreshRereadsWallpaperDesktop),
        ("Native bridge bootstrap marks only wallpaper, /s and /p hosts non-interactive", InteractiveFollowsHostKind),
        ("Native bridge ZMatrix startup prepares once", ZMatrixStartupPreparesOnce),
        ("Native bridge UC3D snapshot precedes show and survives startup", Uc3dSnapshotPrecedesShow),
        ("Native bridge reads granted media off the calling thread", GrantedMediaReadsOffTheCallingThread),
        ("Native bridge rechecks a media grant off the calling thread", MediaGrantRecheckRunsOffTheCallingThread),
        ("Native bridge answers 404 for granted media it cannot read", UnreadableMediaAnswersNotFound),
        ("Native bridge reuses the wallpaper and atlases across a colour-only ZMatrix edit", ColourEditReusesNativeWork),
        ("Native bridge rebuilds only the native work whose input changed", ChangedInputsRebuild),
        ("Native bridge refresh keeps the generation when nothing changed", UnchangedRefreshKeepsGeneration),
        ("Native bridge refresh publishes a changed wallpaper", ChangedWallpaperRefreshPublishes),
        ("Native bridge refresh rebuilds the atlases when font smoothing changes", SmoothingRefreshRebuildsAtlases),
        ("Native bridge builds edit and refresh work off the UI thread", NativeWorkRunsOffTheUiThread),
        ("Native bridge keeps atlas roles apart when their inputs match", BuiltAtlasRolesStayApart),
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

    private static void RefreshRereadsWallpaperDesktop()
    {
        const string query = "scene=uc3d&desktopBackground=true";
        var wallpaper = new Captures();
        using (var bridge = Bridge(NativeHostKind.Wallpaper, wallpaper))
        {
            bridge.Prepare(query, Virtual);
            Refresh(bridge, query);
            Require(wallpaper.Wallpaper + wallpaper.Screen == 2, $"A wallpaper host's refresh must re-read its desktop: {wallpaper.Wallpaper + wallpaper.Screen} reads.");
        }
        // Control: a screen copy is only clean before this window covers the screen, so it is kept.
        var screen = new Captures();
        using (var bridge = Bridge(NativeHostKind.Interactive, screen))
        {
            bridge.Prepare(query, Virtual);
            Refresh(bridge, query);
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

    // R2-53: one granted picture in a fixture folder, requested by the url the bootstrap publishes.
    private static void WithPicture(Action<string, byte[]> test)
    {
        var folder = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "matrixdesktop-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
            File.WriteAllBytes(Path.Combine(folder, "picture.png"), picture);
            test(folder, picture);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static NativeMediaBridge MediaBridge(string folder, Func<MediaEntry, bool>? check = null, Func<string, byte[]>? reader = null)
    {
        var bridge = new NativeMediaBridge(false)
        {
            ScreenCapture = bounds => [1, 2, 3],
            AccessCheck = check ?? MediaCatalog.StillAllowed,
            FileReader = reader ?? File.ReadAllBytes,
        };
        bridge.Prepare("scene=uc3d&showPictures=true&desktopBackground=true&pictureDirectory=" + Uri.EscapeDataString(folder), Small);
        return bridge;
    }

    private static string PictureUrl(NativeMediaBridge bridge)
    {
        var images = Bootstrap(bridge).GetProperty("images");
        Require(images.GetArrayLength() == 1, $"The fixture folder must grant exactly one picture: {images}.");
        return images[0].GetProperty("url").GetString()!;
    }

    // A step of a granted read (the recheck or the read) that blocks until the test opens its gate.
    private sealed class GatedStep : IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new(), Gate = new();
        internal int RanOn;
        internal T Run<T>(Func<T> step)
        {
            RanOn = Environment.CurrentManagedThreadId;
            Entered.Set();
            Gate.Wait(5000);
            return step();
        }
        public void Dispose() { Entered.Dispose(); Gate.Dispose(); }
    }

    // Asks for a url from the stand-in UI thread, as OnResourceRequested does, and opens the
    // gate only after that thread has run again: whether the call returned before the gated step
    // finished, and whether the UI thread ran while that step was still blocked.
    private static (NativeMediaBridge.ResourceAnswer Answer, bool Returned, bool Ran, long Elapsed) AskWhileBlocked(NativeMediaBridge bridge, string url, GatedStep step)
    {
        var returned = false;
        var ran = false;
        long elapsed = 0;
        var answer = UiThread.Run(async () =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var pending = bridge.AnswerAsync(url, "GET");
            elapsed = clock.ElapsedMilliseconds;
            returned = !pending.IsCompleted;
            ran = await Task.Run(() => step.Entered.Wait(5000)) && !pending.IsCompleted;
            step.Gate.Set();
            return await pending;
        });
        return (answer, returned, ran, elapsed);
    }

    // R2-53: requests arrive on the UI thread, which services the low-level input hooks, so a
    // granted picture's answer must come back to that thread while its file read is still blocked.
    private static void GrantedMediaReadsOffTheCallingThread()
    {
        WithPicture((folder, picture) =>
        {
            using var read = new GatedStep();
            using var bridge = MediaBridge(folder, reader: path => read.Run(() => File.ReadAllBytes(path)));
            var url = PictureUrl(bridge);
            var ui = Environment.CurrentManagedThreadId;
            var (answer, returned, ran, elapsed) = AskWhileBlocked(bridge, url, read);
            Require(returned, $"A granted picture's answer must return to the UI thread before its file read finishes; it returned complete after {elapsed} ms.");
            Require(ran && read.RanOn != ui, $"The UI thread must run again while the file is read on another thread: ran during the read={ran}, reader thread={read.RanOn}, UI thread={ui}.");
            Require(answer.Bytes is { } bytes && bytes.SequenceEqual(picture) && answer.ContentType == "image/png",
                $"Once read, the picture must be answered with its own bytes as image/png, not {answer.ContentType}.");
            // Controls: in-memory and ungranted requests are still answered before the call returns.
            var resources = url[..(url.LastIndexOf('/') + 1)];
            var desktop = bridge.AnswerAsync(resources + "desktop", "GET");
            var ungranted = bridge.AnswerAsync(resources + "image-0000000000000000", "GET");
            Require(desktop.IsCompleted && desktop.Result.Bytes is [1, 2, 3] && desktop.Result.ContentType == "image/png",
                "The in-memory desktop snapshot must be answered before the call returns.");
            Require(ungranted.IsCompleted && ungranted.Result.Bytes is null, "An ungranted id must be answered 404 before the call returns.");
        });
    }

    // R2-53: the grant recheck is a metadata query that a remote folder can hold for the SMB
    // timeout, so it runs off the calling thread too, and a grant it revokes is never read.
    private static void MediaGrantRecheckRunsOffTheCallingThread()
    {
        WithPicture((folder, picture) =>
        {
            using var check = new GatedStep();
            var reads = 0;
            using var bridge = MediaBridge(folder, check: entry => check.Run(() => false),
                reader: path => { Interlocked.Increment(ref reads); return File.ReadAllBytes(path); });
            var url = PictureUrl(bridge);
            var ui = Environment.CurrentManagedThreadId;
            var (answer, returned, ran, elapsed) = AskWhileBlocked(bridge, url, check);
            Require(returned, $"A granted picture's answer must return to the UI thread before its grant recheck finishes; it returned complete after {elapsed} ms.");
            Require(ran && check.RanOn != ui, $"The UI thread must run again while the grant is rechecked on another thread: ran during the recheck={ran}, recheck thread={check.RanOn}, UI thread={ui}.");
            Require(answer.Bytes is null && reads == 0, $"A grant the recheck revokes must be answered 404 without reading the file: {reads} reads.");
        });
    }

    // R2-53: a file that cannot be read is answered 404 from the pool thread, not faulted back.
    private static void UnreadableMediaAnswersNotFound()
    {
        WithPicture((folder, picture) =>
        {
            using var bridge = MediaBridge(folder, reader: path => throw new IOException("The fixture picture is locked."));
            var url = PictureUrl(bridge);
            NativeMediaBridge.ResourceAnswer answer;
            try { answer = UiThread.Run(() => bridge.AnswerAsync(url, "GET")); }
            catch (IOException error) { throw new InvalidOperationException($"An unreadable picture must be answered 404, not fault its request: {error.Message}"); }
            Require(answer.Bytes is null && answer.ContentType.StartsWith("text/plain", StringComparison.Ordinal),
                $"An unreadable picture must be answered 404 with the plain-text body, not {answer.ContentType}.");
        });
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
    }

    private static NativeMediaBridge ZMatrixBridge(Work work) => new(false, NativeHostKind.Wallpaper)
    {
        WallpaperCapture = bounds =>
        {
            work.Ran("wallpaper capture");
            Interlocked.Increment(ref work.Wallpaper);
            return new WallpaperBackground.Capture([work.Fill, 5, 6], bounds.Width, bounds.Height, bounds.X, bounds.Y, [0, 0, 0]);
        },
        AtlasFactory = (font, repertoire, special, dpi, path, prefix, url) =>
        {
            work.Ran(special ? "special atlas" : "normal atlas");
            if (special) Interlocked.Increment(ref work.Special); else Interlocked.Increment(ref work.Normal);
            var id = prefix + "-0";
            return new GdiGlyphAtlas.Atlas(new { pages = new[] { new { url = url(id) } }, font, dpi },
                new Dictionary<string, byte[]> { [id] = Encoding.UTF8.GetBytes($"{work.Raster}|{font}|{string.Concat(repertoire)}") });
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
}
