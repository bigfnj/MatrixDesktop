using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace MatrixDesktop.Shared;

// Only generated font/wallpaper resources and explicitly granted UC3D media
// are exposed. There is no directory mapping or filesystem-path request API.
internal sealed class NativeMediaBridge : IDisposable
{
    // WebView2 bypasses WebResourceRequested for virtual-folder mapped hosts.
    // Keep granted media on an intercepted origin, separate from bundled assets.
    private const string ResourceOrigin = "https://uc3d-media.local";
    private readonly bool _preview;
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
    private object? _wallpaper;
    private object? _zmatrixFonts;
    private string? _assetRoot;
    private int _generation;
    private Rectangle _captureBounds;
    private int _atlasDpi = 96;
    private bool _disposed;

    internal NativeMediaBridge(bool preview) => _preview = preview;

    internal void SetAssetRoot(string webRoot)
    {
        if (_assetRoot == webRoot) return;
        _assetRoot = webRoot;
        _configuration = "";
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
        if (options.TryGetValue("scene", out var selectedScene) && selectedScene.Equals("zmatrix", StringComparison.OrdinalIgnoreCase))
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
        else if (_desktop is null)
        {
            try
            {
                if (captureBounds.Width <= 0 || captureBounds.Height <= 0) throw new InvalidOperationException("No display bounds are available.");
                using var bitmap = new Bitmap(captureBounds.Width, captureBounds.Height);
                using (var graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(captureBounds.Location, Point.Empty, captureBounds.Size);
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                _desktop = stream.ToArray();
            }
            catch (Exception ex)
            {
                _warnings.Add($"Desktop snapshot is unavailable: {ex.Message}");
            }
        }
    }

    private void PrepareZMatrix(Dictionary<string, string> options, Rectangle bounds, int dpi)
    {
        try
        {
            var wallpaper = WallpaperBackground.Create(bounds);
            var id = $"wallpaper-{_generation}";
            _nativeResources.Add(id, wallpaper.Png);
            _wallpaper = new { generation = _generation, url = ResourceUrl(id), width = wallpaper.Width, height = wallpaper.Height,
                bounds = new { x = wallpaper.X, y = wallpaper.Y, width = wallpaper.Width, height = wallpaper.Height },
                backgroundColor = wallpaper.BackgroundColor, source = "native-wallpaper", warnings = Array.Empty<string>() };
        }
        catch (Exception error) { _warnings.Add($"Wallpaper background unavailable: {error.Message}"); }
        try
        {
            var characters = ReadTextArray(options.GetValueOrDefault("zmCharacters"), "1234567890ABCDEFGHIJKLMNOPQRSUTVWXYZabcd".Select(c => c.ToString()).ToArray());
            var phrases = ReadTextArray(options.GetValueOrDefault("zmPhrases"), ["The matrix has you"]);
            var special = phrases.SelectMany(text => text.Select(c => c.ToString())).Distinct(StringComparer.Ordinal);
            var asset = Path.Combine(_assetRoot ?? Path.Combine(AppContext.BaseDirectory, "web"), "assets", "zmatrix", "matrix-code.ttf");
            var regularAtlas = GdiGlyphAtlas.Create(DecodeStructured(options.GetValueOrDefault("zmFont")), characters, false, dpi, asset,
                $"font-{_generation}-normal", ResourceUrl);
            var specialAtlas = GdiGlyphAtlas.Create(DecodeStructured(options.GetValueOrDefault("zmSpecialFont")), special, true, dpi, asset,
                $"font-{_generation}-special", ResourceUrl);
            foreach (var (id, bytes) in regularAtlas.Resources.Concat(specialAtlas.Resources)) _nativeResources.Add(id, bytes);
            _zmatrixFonts = new { generation = _generation, normal = regularAtlas.Metadata, special = specialAtlas.Metadata };
        }
        catch (Exception error) { _warnings.Add($"Native ZMatrix fonts unavailable: {error.Message}"); }
    }

    private static string[] ReadTextArray(string? json, string[] fallback)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        return JsonSerializer.Deserialize<string[]>(DecodeStructured(json)!) ?? throw new InvalidOperationException("A ZMatrix text list is invalid.");
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

    internal async Task RefreshAsync(string query, Rectangle bounds, int atlasDpi)
    {
        await _updateLock.WaitAsync();
        try
        {
            if (_disposed) return;
            _configuration = "";
            Prepare(query, bounds, atlasDpi);
            await PublishBootstrapAsync();
        }
        finally { _updateLock.Release(); }
    }

    private async Task PublishBootstrapAsync()
    {
        if (_core is null || _disposed) return;
        var core = _core;
        if (_scriptId is not null) core.RemoveScriptToExecuteOnDocumentCreated(_scriptId);
        var json = JsonSerializer.Serialize(new
        {
            nativeGeneration = _generation,
            configuration = _configuration.Length == 0 ? "" : "?" + _configuration.TrimStart('?'),
            images = _files.Values.Where(f => f.Id.StartsWith("image-", StringComparison.Ordinal))
                .Select(f => new { id = f.Id, url = ResourceUrl(f.Id), caption = f.Caption, group = f.Group, groups = f.Groups ?? [f.Group] }),
            desktopUrl = _desktop is null ? null : ResourceUrl("desktop"),
            audioUrl = _files.ContainsKey("music") ? ResourceUrl("music") : null,
            effectsUrl = _files.ContainsKey("effects") ? ResourceUrl("effects") : null,
            glyphUrl = _files.ContainsKey("glyphs") ? ResourceUrl("glyphs") : null,
            wallpaper = _wallpaper,
            zmatrixFonts = _zmatrixFonts,
            preview = _preview,
            warnings = _warnings,
        });
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
            "window.chrome?.webview?.addEventListener('message',e=>message(e.data));" +
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
        // An in-flight UI request may still be returning from its awaited WebView2 call.
        // Let the semaphore be collected rather than disposing under that request.
    }
}
