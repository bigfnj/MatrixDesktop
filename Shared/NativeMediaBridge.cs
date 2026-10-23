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

// 2026-10-23: only exact resources granted by explicit UC3D media options are
// exposed. There is no directory mapping or filesystem-path request API.
internal sealed class NativeMediaBridge : IDisposable
{
    // WebView2 bypasses WebResourceRequested for virtual-folder mapped hosts.
    // Keep granted media on an intercepted origin, separate from bundled assets.
    private const string ResourceOrigin = "https://uc3d-media.local";
    private readonly bool _preview;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly Dictionary<string, MediaEntry> _files = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];
    private CoreWebView2? _core;
    private string _origin = "";
    private string? _scriptId;
    private string _configuration = "";
    private byte[]? _desktop;
    private bool _disposed;

    internal NativeMediaBridge(bool preview) => _preview = preview;

    internal void Prepare(string query, Rectangle captureBounds)
    {
        if (_disposed || query == _configuration) return;
        _configuration = query;
        _files.Clear();
        _warnings.Clear();
        var options = ParseQuery(query);
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

    internal async Task AttachAsync(CoreWebView2 core, string origin)
    {
        if (_core is not null) _core.WebResourceRequested -= OnResourceRequested;
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

    private async Task PublishBootstrapAsync()
    {
        if (_core is null || _disposed) return;
        if (_scriptId is not null) _core.RemoveScriptToExecuteOnDocumentCreated(_scriptId);
        var json = JsonSerializer.Serialize(new
        {
            images = _files.Values.Where(f => f.Id.StartsWith("image-", StringComparison.Ordinal))
                .Select(f => new { id = f.Id, url = ResourceUrl(f.Id), caption = f.Caption, group = f.Group, groups = f.Groups ?? [f.Group] }),
            desktopUrl = _desktop is null ? null : ResourceUrl("desktop"),
            audioUrl = _files.ContainsKey("music") ? ResourceUrl("music") : null,
            effectsUrl = _files.ContainsKey("effects") ? ResourceUrl("effects") : null,
            glyphUrl = _files.ContainsKey("glyphs") ? ResourceUrl("glyphs") : null,
            preview = _preview,
            warnings = _warnings,
        });
        // The script also runs in child frames. Restrict its data to our exact origin.
        _scriptId = await _core.AddScriptToExecuteOnDocumentCreatedAsync(
            $"if(location.origin==={JsonSerializer.Serialize(_origin)}){{window.matrixHostBootstrap={json};}}");
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
            else if (id == "desktop" && _desktop is not null) { bytes = _desktop; type = "image/png"; }
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
        _desktop = null;
        // An in-flight UI request may still be returning from its awaited WebView2 call.
        // Let the semaphore be collected rather than disposing under that request.
    }
}
