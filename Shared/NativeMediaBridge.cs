using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
    // refresh rebuilds only what changed. Atlases key on everything GDI rasterises from,
    // including the font-smoothing settings that DEFAULT_QUALITY follows. The wallpaper has
    // no readable input, so an environment epoch (a refresh, a preference or display change)
    // expires it, and a re-read that matches keeps its resource id.
    private sealed record WallpaperEntry(Rectangle Bounds, int Epoch, int Built, WallpaperBackground.Capture Capture);
    private readonly record struct AtlasKey(string Asset, int Dpi, string Raster, string? Font, string Repertoire);
    private sealed record AtlasEntry(AtlasKey Key, GdiGlyphAtlas.Atlas Atlas);
    private WallpaperEntry? _wallpaperCache;
    private AtlasEntry? _normalAtlas, _specialAtlas;
    private int _environment;

    internal NativeMediaBridge(bool preview, NativeHostKind hostKind = NativeHostKind.Interactive)
    {
        _preview = preview;
        _hostKind = hostKind;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
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

    internal void Prepare(string query, Rectangle captureBounds, int atlasDpi = 96)
    {
        if (_disposed || (query == _configuration && captureBounds == _captureBounds && atlasDpi == _atlasDpi)) return;
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
        Refreshable = options.TryGetValue("scene", out var selectedScene) && selectedScene.Equals("zmatrix", StringComparison.OrdinalIgnoreCase);
        if (Refreshable)
        {
            _desktop = null;
            PrepareZMatrix(options, captureBounds, atlasDpi);
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

    private void PrepareZMatrix(Dictionary<string, string> options, Rectangle bounds, int dpi)
    {
        // The page decodes the wallpaper only in bitmap mode (zmatrix/runtime.js), and its
        // choice parser lowercases without trimming, so colour mode skips the capture.
        if (!string.Equals(options.GetValueOrDefault("zmBackgroundMode"), "color", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _wallpaper = Wallpaper(bounds);
                _nativeResources.Add($"wallpaper-{_wallpaper.Built}", _wallpaper.Capture.Png);
            }
            catch (Exception error) { _warnings.Add($"Wallpaper background unavailable: {error.Message}"); }
        }
        try
        {
            var characters = ReadTextArray(options.GetValueOrDefault("zmCharacters"), "1234567890ABCDEFGHIJKLMNOPQRSUTVWXYZabcd".Select(c => c.ToString()).ToArray(), phrases: false);
            var phrases = ReadTextArray(options.GetValueOrDefault("zmPhrases"), ["The matrix has you"], phrases: true);
            var special = phrases.SelectMany(text => text.Select(c => c.ToString())).Distinct(StringComparer.Ordinal).ToArray();
            var asset = Path.Combine(_assetRoot ?? Path.Combine(AppContext.BaseDirectory, "web"), "assets", "zmatrix", "matrix-code.ttf");
            var raster = RasterEnvironment();
            // Each role has its own inputs, so an edit to one font rebuilds only that atlas.
            var regular = Atlas(ref _normalAtlas, new AtlasKey(asset, dpi, raster, DecodeStructured(options.GetValueOrDefault("zmFont")), string.Concat(characters)), characters, false);
            var specialAtlas = Atlas(ref _specialAtlas, new AtlasKey(asset, dpi, raster, DecodeStructured(options.GetValueOrDefault("zmSpecialFont")), string.Concat(special)), special, true);
            foreach (var (id, bytes) in regular.Atlas.Resources.Concat(specialAtlas.Atlas.Resources)) _nativeResources.Add(id, bytes);
            _zmatrixFonts = (regular, specialAtlas);
        }
        catch (Exception error) { _warnings.Add($"Native ZMatrix fonts unavailable: {error.Message}"); }
    }

    private WallpaperEntry Wallpaper(Rectangle bounds)
    {
        var epoch = Volatile.Read(ref _environment);
        var cached = _wallpaperCache;
        if (cached is not null && cached.Bounds == bounds && cached.Epoch == epoch) return cached;
        var capture = WallpaperCapture(bounds);
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
        cache = new AtlasEntry(key, AtlasFactory(key.Font, repertoire, special, key.Dpi, key.Asset,
            $"font-{_generation}-{(special ? "special" : "normal")}", ResourceUrl));
        return cache;
    }

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
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
            InvalidateEnvironment();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => InvalidateEnvironment();

    private void InvalidateEnvironment() => Interlocked.Increment(ref _environment);

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
            Prepare(query, bounds);
            await PublishBootstrapAsync();
        }
        finally { _updateLock.Release(); }
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
            var generation = _generation;
            var before = BootstrapJson(0);
            var desktop = _desktop;
            _configuration = "";
            // A wallpaper-sourced desktop is re-read so a wallpaper change reaches a later UC3D load.
            if (UsesWallpaperDesktop) _desktop = null;
            Prepare(query, bounds, atlasDpi);
            // Unchanged inputs reuse their entries, so the bootstrap names the same ids; the
            // UC3D desktop is served under a fixed id, so its bytes are compared.
            if (BootstrapJson(0) == before && (desktop ?? []).AsSpan().SequenceEqual(_desktop ?? []))
            {
                _generation = generation;
                return false;
            }
            await PublishBootstrapAsync();
            return true;
        }
        finally { _updateLock.Release(); }
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

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_core is null || _disposed) return;
        var uri = new Uri(e.Request.Uri);
        var id = uri.AbsolutePath.StartsWith("/__uc3d/", StringComparison.Ordinal) ? uri.AbsolutePath["/__uc3d/".Length..] : "";
        byte[]? bytes = null;
        string type = "text/plain; charset=utf-8";
        try
        {
            if (uri.GetLeftPart(UriPartial.Authority) != ResourceOrigin || e.Request.Method != "GET") { }
            else if (id == "bootstrap" && _bootstrapJson is not null) { bytes = _bootstrapJson; type = "application/json; charset=utf-8"; }
            else if (id == "desktop" && _desktop is not null) { bytes = _desktop; type = "image/png"; }
            else if (_nativeResources.TryGetValue(id, out var native)) { bytes = native; type = "image/png"; }
            else if (_files.TryGetValue(id, out var file) && MediaCatalog.StillAllowed(file))
            {
                bytes = File.ReadAllBytes(file.Path);
                type = file.ContentType;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"UC3D media resource could not be read: {ex.Message}");
        }
        var found = bytes is not null;
        // In-memory responses retain no file handles. WebView2 retains the response
        // stream for consumption; old image data is collected after that response ends.
        var content = new MemoryStream(bytes ?? Encoding.UTF8.GetBytes("Media resource unavailable."), writable: false);
        e.Response = _core.Environment.CreateWebResourceResponse(content, found ? 200 : 404, found ? "OK" : "Not Found",
            $"Content-Type: {type}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nAccess-Control-Allow-Origin: {_origin}\r\n");
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
        // An in-flight UI request may still be returning from its awaited WebView2 call.
        // Let the semaphore be collected rather than disposing under that request.
    }
}
