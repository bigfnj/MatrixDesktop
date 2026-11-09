using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace MatrixDesktop.Shared;

// What kind of window hosts the page. Wallpaper and the screensaver preview sit behind
// other applications, so their UC3D desktop comes from the wallpaper, never the screen.
internal enum NativeHostKind { Interactive, Wallpaper, Screensaver, ScreensaverPreview }

// Only generated font/wallpaper resources and explicitly granted UC3D media
// are exposed. There is no directory mapping or filesystem-path request API.
internal sealed class NativeMediaBridge : IDisposable
{
    // WebView2 bypasses WebResourceRequested for virtual-folder mapped hosts.
    // Keep granted media on an intercepted origin, separate from bundled assets.
    private const string ResourceOrigin = "https://uc3d-media.local";
    private readonly bool _preview;
    private readonly NativeHostKind _hostKind;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly Dictionary<string, MediaEntry> _files = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];
    private readonly Dictionary<string, byte[]> _nativeResources = new(StringComparer.Ordinal);
    private byte[]? _bootstrapJson;
    private CoreWebView2? _core;
    private string _origin = "";
    private string? _scriptId;
    private string _configuration = "";
    private byte[]? _desktop;
    private WallpaperEntry? _wallpaper;
    private (AtlasEntry Normal, AtlasEntry Special)? _zmatrixFonts;
    private string? _assetRoot;
    private int _generation;
    private Rectangle _captureBounds;
    private int _atlasDpi = 96;
    private bool _disposed;

    // 2026-11-08 (AUD-09, AUD-11): native work is cached on its real inputs, so an edit or a
    // refresh rebuilds only what changed. Atlases key on their request (asset, DPI, font,
    // repertoire), the font-smoothing settings that DEFAULT_QUALITY follows, and a font-table
    // epoch (R2-07: a font install or a refresh), because GDI resolves a requested face through
    // the live font table and that has no readable input. The wallpaper has none either, so an
    // environment epoch (a refresh, a preference or display change) expires it. A rebuild or a
    // re-read that matches what is cached keeps its resource ids.
    private sealed record WallpaperEntry(Rectangle Bounds, int Epoch, int Built, WallpaperBackground.Capture Capture);
    private readonly record struct AtlasKey(string Asset, int Dpi, string Raster, int Fonts, string? Font, string Repertoire);
    private sealed record AtlasEntry(AtlasKey Key, GdiGlyphAtlas.Atlas Atlas);
    private WallpaperEntry? _wallpaperCache;
    private AtlasEntry? _normalAtlas, _specialAtlas;
    private int _environment, _fonts;

    // 2026-11-08 (AUD-09): native work an edit or a refresh built on the thread pool, held with
    // the inputs it was built from until Prepare takes it. A failed build is kept and rethrown
    // there, so a fault never escapes as an unobserved task (fatal in CrashDumpWriter).
    private readonly record struct Built<T>(T? Value, ExceptionDispatchInfo? Error) where T : class
    {
        internal static Built<T> Of(Func<T> build)
        {
            try { return new(build(), null); }
            catch (Exception error) { return new(null, ExceptionDispatchInfo.Capture(error)); }
        }
        internal T Result { get { Error?.Throw(); return Value!; } }
    }
    private (Rectangle Bounds, int Epoch, Built<WallpaperBackground.Capture> Capture)? _builtWallpaper;
    private (AtlasKey Key, bool Special, Built<GdiGlyphAtlas.Atlas> Atlas)[] _builtAtlases = [];

    internal NativeMediaBridge(bool preview, NativeHostKind hostKind = NativeHostKind.Interactive)
    {
        _preview = preview;
        _hostKind = hostKind;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.InstalledFontsChanged += OnInstalledFontsChanged;
    }

    // Capture seams for the native harness; production uses the real captures.
    internal Func<Rectangle, byte[]> ScreenCapture { get; init; } = CopyScreen;
    internal Func<Rectangle, WallpaperBackground.Capture> WallpaperCapture { get; init; } = WallpaperBackground.Create;
    internal Func<string?, IEnumerable<string>, bool, int, string?, string, Func<string, string>, GdiGlyphAtlas.Atlas> AtlasFactory { get; init; } = GdiGlyphAtlas.Create;
    internal Func<string> RasterEnvironment { get; init; } = ReadRasterEnvironment;

    // Only the ZMatrix page applies new native resources while it runs; UC3D reads its
    // bootstrap once and Matrix has none, so a refresh cannot change what they show.
    internal bool Refreshable { get; private set; }

    private bool UsesWallpaperDesktop => _hostKind is NativeHostKind.Wallpaper or NativeHostKind.ScreensaverPreview;

    internal void SetAssetRoot(string webRoot)
    {
        if (_assetRoot == webRoot) return;
        _assetRoot = webRoot;
        _configuration = "";
    }

    // Before the window is shown, take only what must precede it: the UC3D desktop
    // snapshot, which a screen copy would otherwise take of this window. Prepare does
    // the rest once the asset root is known, so ZMatrix work is never built twice.
    internal void CaptureDesktopBeforeShow(string query, Rectangle captureBounds)
    {
        if (_disposed || _desktop is not null) return;
        var options = ParseQuery(query);
        if (options.TryGetValue("scene", out var scene) && scene.Equals("uc3d", StringComparison.OrdinalIgnoreCase) && Enabled(options, "desktopBackground"))
            CaptureDesktop(captureBounds);
    }

    internal void Prepare(string query, Rectangle captureBounds, int atlasDpi = 96) => Prepare(query, captureBounds, atlasDpi, ReadInputs());

    private void Prepare(string query, Rectangle captureBounds, int atlasDpi, Inputs inputs)
    {
        if (_disposed || Unchanged(query, captureBounds, atlasDpi, inputs)) return;
        _configuration = query;
        _captureBounds = captureBounds;
        _atlasDpi = atlasDpi;
        _files.Clear();
        _warnings.Clear();
        _nativeResources.Clear();
        _wallpaper = null;
        _zmatrixFonts = null;
        _generation++;
        var options = ParseQuery(query);
        Refreshable = IsZMatrix(options);
        if (Refreshable)
        {
            _desktop = null;
            PrepareZMatrix(options, captureBounds, atlasDpi, inputs);
            return;
        }
        if (!options.TryGetValue("scene", out var scene) || !scene.Equals("uc3d", StringComparison.OrdinalIgnoreCase))
        {
            _desktop = null;
            return;
        }
        if (Enabled(options, "showPictures"))
        {
            foreach (var entry in MediaCatalog.Images(options.GetValueOrDefault("pictureDirectory"), options.GetValueOrDefault("donorDirectory"), _warnings))
                _files.Add(entry.Id, entry);
            if (_files.Count == 0) _warnings.Add("No pictures are available. Choose a picture folder or import a local donor image pack.");
        }
        if (string.Equals(options.GetValueOrDefault("sound"), "music", StringComparison.OrdinalIgnoreCase))
        {
            var audio = MediaCatalog.Audio(options.GetValueOrDefault("musicFile"), _warnings);
            if (audio is not null) _files.Add(audio.Id, audio);
        }
        if (Enabled(options, "donorGlyphs"))
        {
            var glyphs = MediaCatalog.Glyphs(options.GetValueOrDefault("donorDirectory"), _warnings);
            if (glyphs is not null) _files.Add(glyphs.Id, glyphs);
        }
        if (string.Equals(options.GetValueOrDefault("sound"), "effects", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(options.GetValueOrDefault("donorDirectory")))
        {
            var effectsPath = Path.Combine(options["donorDirectory"], "code-sizzle.wav");
            if (File.Exists(effectsPath))
            {
                var effects = MediaCatalog.Audio(effectsPath, _warnings);
                if (effects is not null) _files.Add("effects", effects with { Id = "effects" });
            }
        }
        if (!Enabled(options, "desktopBackground")) _desktop = null;
        else if (_desktop is null) CaptureDesktop(captureBounds);
    }

    // A screen copy would bake other applications' windows into a wallpaper or preview,
    // so those hosts paint the wallpaper instead, as ZMatrix does.
    private void CaptureDesktop(Rectangle captureBounds)
    {
        try
        {
            if (captureBounds.Width <= 0 || captureBounds.Height <= 0) throw new InvalidOperationException("No display bounds are available.");
            _desktop = UsesWallpaperDesktop ? WallpaperCapture(captureBounds).Png : ScreenCapture(captureBounds);
        }
        catch (Exception ex)
        {
            _warnings.Add($"Desktop snapshot is unavailable: {ex.Message}");
        }
    }

    private static byte[] CopyScreen(Rectangle captureBounds)
    {
        using var bitmap = new Bitmap(captureBounds.Width, captureBounds.Height);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(captureBounds.Location, Point.Empty, captureBounds.Size);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private void PrepareZMatrix(Dictionary<string, string> options, Rectangle bounds, int dpi, Inputs inputs)
    {
        if (WantsWallpaper(options))
        {
            try
            {
                _wallpaper = Wallpaper(bounds, inputs.Epoch);
                _nativeResources.Add($"wallpaper-{_wallpaper.Built}", _wallpaper.Capture.Png);
            }
            catch (Exception error) { _warnings.Add($"Wallpaper background unavailable: {error.Message}"); }
        }
        try
        {
            var (normal, special) = AtlasInputs(options, dpi, inputs);
            var regular = Atlas(ref _normalAtlas, normal.Key, normal.Repertoire, false);
            var specialAtlas = Atlas(ref _specialAtlas, special.Key, special.Repertoire, true);
            foreach (var (id, bytes) in regular.Atlas.Resources.Concat(specialAtlas.Atlas.Resources)) _nativeResources.Add(id, bytes);
            _zmatrixFonts = (regular, specialAtlas);
        }
        catch (Exception error) { _warnings.Add($"Native ZMatrix fonts unavailable: {error.Message}"); }
    }

    private static bool IsZMatrix(Dictionary<string, string> options) =>
        options.TryGetValue("scene", out var scene) && scene.Equals("zmatrix", StringComparison.OrdinalIgnoreCase);

    // The page decodes the wallpaper only in bitmap mode (zmatrix/runtime.js), and its
    // choice parser lowercases without trimming, so colour mode skips the capture.
    private static bool WantsWallpaper(Dictionary<string, string> options) =>
        !string.Equals(options.GetValueOrDefault("zmBackgroundMode"), "color", StringComparison.OrdinalIgnoreCase);

    // Both atlas roles with everything GDI rasterises them from. Each role has its own
    // inputs, so an edit to one font rebuilds only that atlas.
    private ((AtlasKey Key, string[] Repertoire) Normal, (AtlasKey Key, string[] Repertoire) Special) AtlasInputs(Dictionary<string, string> options, int dpi, Inputs inputs)
    {
        var characters = ReadTextArray(options.GetValueOrDefault("zmCharacters"), "1234567890ABCDEFGHIJKLMNOPQRSUTVWXYZabcd".Select(c => c.ToString()).ToArray(), phrases: false);
        var phrases = ReadTextArray(options.GetValueOrDefault("zmPhrases"), ["The matrix has you"], phrases: true);
        var special = phrases.SelectMany(text => text.Select(c => c.ToString())).Distinct(StringComparer.Ordinal).ToArray();
        var asset = Path.Combine(_assetRoot ?? Path.Combine(AppContext.BaseDirectory, "web"), "assets", "zmatrix", "matrix-code.ttf");
        return ((new AtlasKey(asset, dpi, inputs.Raster, inputs.Fonts, DecodeStructured(options.GetValueOrDefault("zmFont")), string.Concat(characters)), characters),
            (new AtlasKey(asset, dpi, inputs.Raster, inputs.Fonts, DecodeStructured(options.GetValueOrDefault("zmSpecialFont")), string.Concat(special)), special));
    }

    // The same request is unchanged only while the native work it applied is still current: the
    // configurator re-sends its query when the preview is shown, after any wallpaper change, font
    // install or smoothing change. Both atlas roles are keyed on the same environment.
    private bool Unchanged(string query, Rectangle bounds, int dpi, Inputs inputs) => query == _configuration && bounds == _captureBounds && dpi == _atlasDpi
        && (_wallpaper is null || _wallpaper.Epoch == inputs.Epoch)
        && (_zmatrixFonts is not { } fonts || (fonts.Normal.Key.Fonts == inputs.Fonts && fonts.Normal.Key.Raster == inputs.Raster));

    // 2026-11-08 (R2-08): the environment native work is built from. A call reads it on the UI
    // thread before each build pass and hands Prepare the inputs of its last pass, so a broadcast
    // that arrives during a build never makes Prepare build on the UI thread.
    private readonly record struct Inputs(int Epoch, int Fonts, string Raster);
    private Inputs ReadInputs() => new(Volatile.Read(ref _environment), Volatile.Read(ref _fonts), RasterEnvironment());
    private const int BuildPasses = 3;

    // 2026-11-08 (AUD-09): the wallpaper capture with its PNG encode and both GDI atlases take
    // most of a second on a large virtual screen. An edit or a refresh therefore builds what the
    // next Prepare would miss on the thread pool and applies nothing there; Prepare then runs on
    // the caller's (UI) thread and takes these results instead of building them itself. Prepare
    // follows with exactly one generation step, so atlas ids minted here carry its generation.
    // A preference, display or smoothing change during the awaits can expire what a pass built
    // or a cached entry it relied on, so another pass builds just that, up to BuildPasses. If the
    // inputs still move, Prepare applies the last pass under its own inputs, and the next refresh
    // or edit catches up (Unchanged sees the expired wallpaper).
    private async Task<Inputs> BuildAheadAsync(string query, Rectangle bounds, int dpi)
    {
        var options = ParseQuery(query);
        var inputs = ReadInputs();
        if (_disposed || Unchanged(query, bounds, dpi, inputs) || !IsZMatrix(options)) return inputs;
        var generation = _generation + 1;
        for (var pass = 1; ; pass++)
        {
            var epoch = inputs.Epoch;
            var wallpaper = WantsWallpaper(options) && CachedWallpaper(bounds, epoch) is null && BuiltWallpaper(bounds, epoch) is null
                ? Task.Run(() => Built<WallpaperBackground.Capture>.Of(() => WallpaperCapture(bounds))) : null;
            var roles = new List<(AtlasKey Key, string[] Repertoire, bool Special)>();
            try
            {
                var (normal, special) = AtlasInputs(options, dpi, inputs);
                if (_normalAtlas?.Key != normal.Key && BuiltAtlas(normal.Key, false) < 0) roles.Add((normal.Key, normal.Repertoire, false));
                if (_specialAtlas?.Key != special.Key && BuiltAtlas(special.Key, true) < 0) roles.Add((special.Key, special.Repertoire, true));
            }
            catch (Exception) { } // Prepare meets the same malformed input and reports it.
            if (wallpaper is null && roles.Count == 0) return inputs;
            // The roles build in order on one thread, as Prepare would build them.
            var atlases = roles.Count == 0 ? null : Task.Run(() => roles.Select(role => (role.Key, role.Special, Built<GdiGlyphAtlas.Atlas>.Of(() =>
                AtlasFactory(role.Key.Font, role.Repertoire, role.Special, role.Key.Dpi, role.Key.Asset, AtlasPrefix(generation, role.Special), ResourceUrl)))).ToArray());
            if (wallpaper is not null) _builtWallpaper = (bounds, epoch, await wallpaper);
            if (atlases is not null)
            {
                var made = await atlases;
                _builtAtlases = [.. _builtAtlases, .. made];
            }
            var current = ReadInputs();
            if (_disposed || pass == BuildPasses || current == inputs) return inputs;
            inputs = current;
        }
    }

    private Built<WallpaperBackground.Capture>? BuiltWallpaper(Rectangle bounds, int epoch) =>
        _builtWallpaper is { } built && built.Bounds == bounds && built.Epoch == epoch ? built.Capture : null;

    private int BuiltAtlas(AtlasKey key, bool special) => Array.FindIndex(_builtAtlases, entry => entry.Key == key && entry.Special == special);

    private void DropBuilt()
    {
        _builtWallpaper = null;
        _builtAtlases = [];
    }

    private WallpaperEntry? CachedWallpaper(Rectangle bounds, int epoch) =>
        _wallpaperCache is { } cached && cached.Bounds == bounds && cached.Epoch == epoch ? cached : null;

    private WallpaperEntry Wallpaper(Rectangle bounds, int epoch)
    {
        if (CachedWallpaper(bounds, epoch) is { } hit) return hit;
        var cached = _wallpaperCache;
        var capture = BuiltWallpaper(bounds, epoch) is { } built ? built.Result : WallpaperCapture(bounds);
        _wallpaperCache = cached is not null && cached.Bounds == bounds && SameCapture(cached.Capture, capture)
            ? cached with { Epoch = epoch }
            : new WallpaperEntry(bounds, epoch, _generation, capture);
        return _wallpaperCache;
    }

    private static bool SameCapture(WallpaperBackground.Capture a, WallpaperBackground.Capture b) =>
        a.Width == b.Width && a.Height == b.Height && a.X == b.X && a.Y == b.Y
        && a.BackgroundColor.AsSpan().SequenceEqual(b.BackgroundColor) && a.Png.AsSpan().SequenceEqual(b.Png);

    // Resource ids carry the generation that built them, so an id never names other bytes.
    private AtlasEntry Atlas(ref AtlasEntry? cache, AtlasKey key, string[] repertoire, bool special)
    {
        if (cache is not null && cache.Key == key) return cache;
        var built = BuiltAtlas(key, special);
        var atlas = built >= 0 ? _builtAtlases[built].Atlas.Result
            : AtlasFactory(key.Font, repertoire, special, key.Dpi, key.Asset, AtlasPrefix(_generation, special), ResourceUrl);
        // A rebuild that draws what is cached (a refresh, or a font install that left this face
        // alone) keeps the cached ids under the new key, so nothing is republished.
        cache = cache is not null && SameAtlas(cache.Atlas, atlas) ? cache with { Key = key } : new AtlasEntry(key, atlas);
        return cache;
    }

    // Ids carry the generation that built them, so two atlases match when their pages hold the
    // same bytes in id order (one prefix per atlas) and their metadata matches without page urls.
    private static bool SameAtlas(GdiGlyphAtlas.Atlas a, GdiGlyphAtlas.Atlas b)
    {
        static IEnumerable<byte[]> Pages(GdiGlyphAtlas.Atlas atlas) => atlas.Resources.OrderBy(page => page.Key, StringComparer.Ordinal).Select(page => page.Value);
        static JsonNode? Content(GdiGlyphAtlas.Atlas atlas)
        {
            var node = JsonSerializer.SerializeToNode(atlas.Metadata);
            if (node?["pages"] is JsonArray pages)
                foreach (var page in pages.OfType<JsonObject>()) page.Remove("url");
            return node;
        }
        return a.Resources.Count == b.Resources.Count && Pages(a).Zip(Pages(b)).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second))
            && JsonNode.DeepEquals(Content(a), Content(b));
    }

    private static string AtlasPrefix(int generation, bool special) => $"font-{generation}-{(special ? "special" : "normal")}";

    // GDI rasterises DEFAULT_QUALITY text with the current smoothing settings
    // (GdiGlyphAtlas quality 0), so they are an atlas input.
    private static string ReadRasterEnvironment()
    {
        static uint Read(uint action) { uint value = 0; return SystemParametersInfo(action, 0, ref value, 0) ? value : uint.MaxValue; }
        // SPI_GETFONTSMOOTHING, ...TYPE, ...CONTRAST, ...ORIENTATION
        return $"{Read(0x004A)}/{Read(0x200A)}/{Read(0x200C)}/{Read(0x2012)}";
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref uint value, uint update);

    // The configurator never refreshes, so preference and display changes expire the wallpaper here.
    internal void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
            InvalidateEnvironment();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => InvalidateEnvironment();

    private void InvalidateEnvironment() => Interlocked.Increment(ref _environment);

    // WM_FONTCHANGE: an installed or removed font can change the face GDI resolves for a name.
    internal void OnInstalledFontsChanged(object? sender, EventArgs e) => Interlocked.Increment(ref _fonts);

    // 2026-11-08: the list rules of zmatrix/settings.js (BMP only, no C0 or DEL, the size
    // limits). config.js silently replaces a rejected list with its default, so the atlas
    // must do the same, or the page requests glyphs this atlas never rasterised.
    private static string[] ReadTextArray(string? json, string[] fallback, bool phrases)
    {
        if (string.IsNullOrWhiteSpace(json) || !global::Shared.StructuredArgumentCodec.TryDecode(json, out var decoded)) return fallback;
        string?[]? values;
        try { values = JsonSerializer.Deserialize<string?[]>(decoded); }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return fallback; }
        if (values is null || values.Any(text => text is null || text.Any(c => c < 0x20 || c == 0x7f || char.IsSurrogate(c)))) return fallback;
        var texts = values.Select(text => text!).ToArray();
        if (phrases) return texts.Length > 64 || texts.Any(text => text.Length is < 1 or > 256) || texts.SelectMany(text => text).Distinct().Count() > 1024 ? fallback : texts;
        return texts.Length is < 1 or > 512 || texts.Any(text => text.Length != 1) ? fallback : texts;
    }

    private static string? DecodeStructured(string? value)
    {
        if (value is null) return null;
        if (!global::Shared.StructuredArgumentCodec.TryDecode(value, out var decoded)) throw new InvalidOperationException("A structured ZMatrix value is not valid UTF-8 JSON encoding.");
        return decoded;
    }

    internal async Task AttachAsync(CoreWebView2 core, string origin)
    {
        if (_disposed) return;
        if (_core is not null)
        {
            try { _core.WebResourceRequested -= OnResourceRequested; }
            catch (InvalidOperationException) { }
        }
        _core = core;
        _origin = origin.TrimEnd('/');
        core.AddWebResourceRequestedFilter(ResourceOrigin + "/__uc3d/*", CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnResourceRequested;
        await PublishBootstrapAsync();
    }

    internal async Task UpdateAsync(string query, Rectangle bounds)
    {
        await _updateLock.WaitAsync();
        try
        {
            if (_disposed) return;
            var generation = _generation;
            var before = BootstrapJson(0);
            var inputs = await BuildAheadAsync(query, bounds, 96);
            Prepare(query, bounds, 96, inputs);
            // As in a refresh, an update that changed nothing keeps its generation and is not
            // republished, because the ZMatrix page discards its raster on every bootstrap it applies.
            if (BootstrapJson(0) == before)
            {
                _generation = generation;
                return;
            }
            await PublishBootstrapAsync();
        }
        finally { DropBuilt(); _updateLock.Release(); }
    }

    // A refresh re-reads every input and publishes only a change: the ZMatrix page discards
    // its accumulated raster on every bootstrap it applies. Returns whether it published.
    internal async Task<bool> RefreshAsync(string query, Rectangle bounds, int atlasDpi)
    {
        await _updateLock.WaitAsync();
        try
        {
            if (_disposed) return false;
            InvalidateEnvironment();
            // The font table too: a font installed without a broadcast is resolved by a refresh.
            Interlocked.Increment(ref _fonts);
            var generation = _generation;
            var before = BootstrapJson(0);
            _configuration = "";
            // The published resources stay served while the work builds; only Prepare replaces them.
            var inputs = await BuildAheadAsync(query, bounds, atlasDpi);
            if (_disposed) return false;
            // Only a Refreshable (ZMatrix) bridge is refreshed, so a UC3D desktop, a still
            // snapshot, is never re-read here.
            Prepare(query, bounds, atlasDpi, inputs);
            // Unchanged inputs reuse their entries, so the bootstrap names the same ids.
            if (BootstrapJson(0) == before)
            {
                _generation = generation;
                return false;
            }
            await PublishBootstrapAsync();
            return true;
        }
        finally { DropBuilt(); _updateLock.Release(); }
    }

    private async Task PublishBootstrapAsync()
    {
        if (_core is null || _disposed) return;
        var core = _core;
        if (_scriptId is not null) core.RemoveScriptToExecuteOnDocumentCreated(_scriptId);
        var json = BootstrapJson();
        // Snapshot before awaiting WebView2 registration. Child documents fetch this
        // current snapshot rather than depending on a frame's captured init script.
        _bootstrapJson = Encoding.UTF8.GetBytes(json);
        // The script also runs in child frames. Restrict its data to our exact origin.
        var expectedOrigin = JsonSerializer.Serialize(_origin);
        var initialize = $"if(location.origin==={expectedOrigin}){{const initial={json};" +
            "const matches=bootstrap=>bootstrap?.configuration===location.search;if(matches(initial))window.matrixHostBootstrap=initial;" +
            "const apply=bootstrap=>{if(!matches(bootstrap))return window.matrixHostBootstrap;if(Number(bootstrap?.nativeGeneration??0)<Number(window.matrixHostBootstrap?.nativeGeneration??0))return window.matrixHostBootstrap;" +
            "window.matrixHostBootstrap=bootstrap;window.dispatchEvent(new CustomEvent('matrix-native-resources-updated',{detail:bootstrap}));return window.matrixHostBootstrap;};" +
            "if(!window.__matrixNativeMessages){window.__matrixNativeMessages=true;" +
            "const message=data=>{if(data?.type==='matrix-native-resources')apply(data.bootstrap);};" +
            "window.addEventListener('message',e=>{if(e.source===parent&&e.origin==='https://configurator.local')message(e.data);});}" +
            $"window.matrixHostBootstrapReady=fetch('{ResourceOrigin}/__uc3d/bootstrap',{{cache:'no-store'}})" +
            ".then(response=>{if(!response.ok)throw new Error('Native resource bootstrap failed ('+response.status+')');return response.json();})" +
            ".then(bootstrap=>{if(!matches(bootstrap))throw new Error('Native resources belong to another configuration');return apply(bootstrap);});" +
            "window.matrixHostBootstrapReady.catch(()=>{});}";
        _scriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(initialize);
        if (_disposed || !ReferenceEquals(_core, core)) return;
        // Update a running app directly, or forward to the exact-origin configurator preview.
        var update = $"{{type:'matrix-native-resources',bootstrap:{json}}}";
        await core.ExecuteScriptAsync($"if(location.origin==={expectedOrigin}){{const bootstrap={json};if(bootstrap.configuration===location.search&&Number(bootstrap.nativeGeneration)>=Number(window.matrixHostBootstrap?.nativeGeneration??0)){{window.matrixHostBootstrap=bootstrap;window.dispatchEvent(new CustomEvent('matrix-native-resources-updated',{{detail:bootstrap}}));}}}}" +
            $"else{{for(const frame of document.querySelectorAll('iframe'))frame.contentWindow?.postMessage({update},{expectedOrigin});}}");
    }

    private string ResourceUrl(string id) => ResourceOrigin + "/__uc3d/" + id;

    // The document's bootstrap. Internal so the native harness can read it without a WebView.
    internal string BootstrapJson() => BootstrapJson(_generation);

    // The bytes this bridge serves for a native resource id, for the native harness.
    internal byte[]? Resource(string id) => _nativeResources.GetValueOrDefault(id);

    // Generations are written here, not stored in cached entries, so a reused entry is
    // published under the current generation.
    private string BootstrapJson(int generation) => JsonSerializer.Serialize(new
    {
        nativeGeneration = generation,
        configuration = _configuration.Length == 0 ? "" : "?" + _configuration.TrimStart('?'),
        images = _files.Values.Where(f => f.Id.StartsWith("image-", StringComparison.Ordinal))
            .Select(f => new { id = f.Id, url = ResourceUrl(f.Id), caption = f.Caption, group = f.Group, groups = f.Groups ?? [f.Group] }),
        desktopUrl = _desktop is null ? null : ResourceUrl("desktop"),
        audioUrl = _files.ContainsKey("music") ? ResourceUrl("music") : null,
        effectsUrl = _files.ContainsKey("effects") ? ResourceUrl("effects") : null,
        glyphUrl = _files.ContainsKey("glyphs") ? ResourceUrl("glyphs") : null,
        wallpaper = _wallpaper is { Capture: var capture } wallpaper
            ? new { generation, url = ResourceUrl($"wallpaper-{wallpaper.Built}"), width = capture.Width, height = capture.Height,
                bounds = new { x = capture.X, y = capture.Y, width = capture.Width, height = capture.Height },
                backgroundColor = capture.BackgroundColor, source = "native-wallpaper", warnings = Array.Empty<string>() }
            : null,
        zmatrixFonts = _zmatrixFonts is { } fonts ? new { generation, normal = fonts.Normal.Atlas.Metadata, special = fonts.Special.Atlas.Metadata } : null,
        preview = _preview,
        // No input reaches these windows, so the page must not wait for a click.
        interactive = _hostKind is not (NativeHostKind.Wallpaper or NativeHostKind.Screensaver or NativeHostKind.ScreensaverPreview),
        warnings = _warnings,
    });

    // 2026-11-08 (R2-53): WebView2 raises this on the UI thread, which also services the wallpaper's
    // low-level mouse hook and the fullscreen keyboard-exit hook. Reading a granted file of up to
    // 32 MiB from a disk that is spinning up, or from a remote folder, can hold that thread past the
    // hook timeout, and Windows then removes the hook without telling the app. A file is therefore
    // answered off the UI thread while a deferral holds the request; in-memory resources are still
    // answered before the handler returns.
    // This is async void, so nothing may escape it to Application.ThreadException and the crash dialog.
    private async void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var core = _core;
        if (core is null || _disposed) return;
        try
        {
            var pending = AnswerAsync(e.Request.Uri, e.Request.Method);
            using var deferral = pending.IsCompleted ? null : e.GetDeferral();
            var (bytes, type) = await pending;
            // The surface may have closed, or moved to a new WebView, during the read.
            if (_disposed || !ReferenceEquals(core, _core)) return;
            var found = bytes is not null;
            // In-memory responses retain no file handles. WebView2 retains the response
            // stream for consumption; old image data is collected after that response ends.
            var content = new MemoryStream(bytes ?? Encoding.UTF8.GetBytes("Media resource unavailable."), writable: false);
            e.Response = core.Environment.CreateWebResourceResponse(content, found ? 200 : 404, found ? "OK" : "Not Found",
                $"Content-Type: {type}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nAccess-Control-Allow-Origin: {_origin}\r\n");
        }
        catch (Exception ex)
        {
            Logger.Warn($"UC3D media request could not be answered: {ex.Message}");
        }
    }

    // What a request is answered with; null bytes answer 404.
    internal readonly record struct ResourceAnswer(byte[]? Bytes, string ContentType);

    // Seams for the native harness: the recheck of a media grant and the read of its file.
    internal Func<MediaEntry, bool> AccessCheck { get; init; } = MediaCatalog.StillAllowed;
    internal Func<string, byte[]> FileReader { get; init; } = File.ReadAllBytes;

    // The answer to one request, internal so the native harness can drive it without a WebView.
    // Only a granted file leaves the calling thread: the recheck is a metadata query that a remote
    // folder can hold for the SMB timeout, and the read can wait for a disk to spin up.
    internal async Task<ResourceAnswer> AnswerAsync(string url, string method)
    {
        var uri = new Uri(url);
        var id = uri.AbsolutePath.StartsWith("/__uc3d/", StringComparison.Ordinal) ? uri.AbsolutePath["/__uc3d/".Length..] : "";
        byte[]? bytes = null;
        string type = "text/plain; charset=utf-8";
        if (uri.GetLeftPart(UriPartial.Authority) != ResourceOrigin || method != "GET") { }
        else if (id == "bootstrap" && _bootstrapJson is not null) { bytes = _bootstrapJson; type = "application/json; charset=utf-8"; }
        else if (id == "desktop" && _desktop is not null) { bytes = _desktop; type = "image/png"; }
        else if (_nativeResources.TryGetValue(id, out var native)) { bytes = native; type = "image/png"; }
        else if (_files.TryGetValue(id, out var file))
        {
            bytes = await Task.Run(() => ReadGranted(file)).ConfigureAwait(false);
            if (bytes is not null) type = file.ContentType;
        }
        return new(bytes, type);
    }

    private byte[]? ReadGranted(MediaEntry file)
    {
        try { return AccessCheck(file) ? FileReader(file.Path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"UC3D media resource could not be read: {ex.Message}");
            return null;
        }
    }

    private static bool Enabled(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && FlagNormalization.ParseBool(value, false);

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            result[WebUtility.UrlDecode(pair[0])] = pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : "true";
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.InstalledFontsChanged -= OnInstalledFontsChanged;
        if (_core is not null)
        {
            try { _core.WebResourceRequested -= OnResourceRequested; } catch (InvalidOperationException) { }
        }
        _core = null;
        _files.Clear();
        _nativeResources.Clear();
        _bootstrapJson = null;
        _desktop = null;
        _zmatrixFonts = null;
        _wallpaper = null;
        _wallpaperCache = null;
        _normalAtlas = _specialAtlas = null;
        DropBuilt();
        // An in-flight UI request may still be returning from its awaited WebView2 call.
        // Let the semaphore be collected rather than disposing under that request.
    }
}
