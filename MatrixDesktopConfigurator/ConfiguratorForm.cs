using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MatrixDesktopConfigurator;

public sealed class ConfiguratorForm : Form
{
    private const string HostName = "configurator.local";
    private const string HostOrigin = "https://configurator.local/";
    private const int CatastrophicFailureHResult = unchecked((int)0x8000FFFF);

    private readonly ConfiguratorMetadata _metadata = ArgumentCatalog.Create();
    private readonly StorageService _storage = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly CommandBuilder _commandBuilder;
    private readonly ArgumentImporter _argumentImporter;
    private readonly DraftRandomizer _randomizer = new();
    private ConfiguratorState _state;
    private WebView2 _webView;
    private Process? _testProcess;
    private bool _isShuttingDown;
    private AppWindowIcon? _windowIcon;

    public ConfiguratorForm()
    {
        Text = "MatrixDesktop Configurator";
        MinimumSize = new System.Drawing.Size(980, 720);
        ClientSize = new System.Drawing.Size(1240, 820);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(12, 16, 18);
        TryApplyWindowIcon();

        _commandBuilder = new CommandBuilder(_metadata);
        _argumentImporter = new ArgumentImporter(_metadata);
        _state = _storage.Load();
        if (_state.LastDraft.Count == 0)
        {
            _state.LastDraft = _commandBuilder.CreateDefaultDraft();
        }

        if (PresetSeeder.Seed(_state, _commandBuilder, _argumentImporter))
        {
            _storage.Save(_state);
        }

        _webView = CreateWebView();
        Controls.Add(_webView);
        Shown += async (_, _) =>
        {
            TryApplyNativeWindowIcons();
            await InitializeWebViewAsync();
        };
    }

    private void TryApplyWindowIcon()
    {
        try
        {
            _windowIcon ??= AppWindowIcon.Load();
            _windowIcon.ApplyTo(this);
        }
        catch
        {
            // Cosmetic only; do not block the configurator if icon extraction fails.
        }
    }

    private void TryApplyNativeWindowIcons()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            _windowIcon?.ApplyTo(this);
        }
        catch
        {
            // Cosmetic only.
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryApplyNativeWindowIcons();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _isShuttingDown = true;
        StopTestProcess();

        // Close the live-preview window if one is open. We assigned a
        // FormClosed handler when creating it that nulls _previewWindow,
        // but explicitly closing here ensures no orphaned WebView2 process
        // is left behind when the user dismisses the configurator.
        try
        {
            if (_previewWindow is { IsDisposed: false })
            {
                _previewWindow.Close();
            }
        }
        catch { /* ignore */ }
        _previewWindow = null;

        try
        {
            _storage.Save(_state);
        }
        catch
        {
            // Ignore last-moment save failures; normal save paths report errors to the UI.
        }

        try
        {
            _webView.Dispose();
        }
        catch
        {
            // Ignore.
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DisposeWindowIcons();
        base.OnFormClosed(e);
    }

    private void DisposeWindowIcons()
    {
        try
        {
            _windowIcon?.Dispose();
        }
        catch
        {
            // Ignore.
        }

        _windowIcon = null;
    }

    private static WebView2 CreateWebView()
    {
        return new WebView2
        {
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(12, 16, 18),
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(12, 16, 18),
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = GetConfiguratorAppDataFolder("WebView2"),
            },
        };
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await InitializeCurrentWebViewAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "Microsoft Edge WebView2 Runtime is not installed on this machine.\n\nInstall the Evergreen WebView2 Runtime and re-run this app.",
                "MatrixDesktop Configurator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
        catch (COMException ex) when (ex.HResult == CatastrophicFailureHResult)
        {
            MessageBox.Show(
                "WebView2 failed to initialize for the configurator.\n\n" + ex,
                "MatrixDesktop Configurator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "MatrixDesktop Configurator", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task InitializeCurrentWebViewAsync()
    {
        if (!_webView.IsHandleCreated)
        {
            _ = _webView.Handle;
        }

        await _webView.EnsureCoreWebView2Async();

        TryTightenWebViewSettings();

        _webView.CoreWebView2.WebMessageReceived += async (_, args) => await HandleWebMessageAsync(args.WebMessageAsJson);
        _webView.CoreWebView2.NavigationStarting += (_, args) =>
        {
            var uri = args.Uri ?? string.Empty;
            if (uri.StartsWith(HostOrigin, StringComparison.OrdinalIgnoreCase) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            args.Cancel = true;
            TryOpenExternal(uri);
        };
        _webView.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            TryOpenExternal(args.Uri);
        };

        var root = GetConfiguratorWebRoot();
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            HostName,
            root,
            CoreWebView2HostResourceAccessKind.Allow);

        _webView.CoreWebView2.Navigate($"{HostOrigin}index.html");
    }

    private async Task HandleWebMessageAsync(string json)
    {
        if (_isShuttingDown)
        {
            return;
        }

        string id = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
            var payload = root.TryGetProperty("payload", out var payloadElement) ? payloadElement : default;

            var result = await DispatchAsync(type, payload);
            await RespondAsync(id, true, result);
        }
        catch (Exception ex)
        {
            await RespondAsync(id, false, new { message = ex.Message });
        }
    }

    private async Task<object?> DispatchAsync(string type, JsonElement payload)
    {
        return type switch
        {
            "loadState"        => LoadStatePayload(),
            "saveDraft"        => SaveDraft(payload),
            "savePreset"       => SavePreset(payload),
            "deletePreset"     => DeletePreset(payload),
            "buildCommand"     => BuildCommand(payload),
            "importCommand"    => ImportCommand(payload),
            "randomizeDraft"   => RandomizeDraft(payload),
            "copyCommand"      => CopyCommand(payload),
            "testCommand"      => TestCommand(payload),
            "stopTest"         => StopTest(),
            // v1.0 additions
            "setTheme"         => SetTheme(payload),
            "exportPowerShell" => ExportPowerShell(payload),
            "openPreview"      => await OpenPreviewAsync(),
            "closePreview"     => ClosePreview(),
            "previewCommand"   => await PreviewCommandAsync(payload),
            "loadHelp"         => LoadHelp(),
            _ => throw new InvalidOperationException($"Unknown configurator request: {type}"),
        };
    }

    // ─── v1.0 dispatchers ───────────────────────────────────────────────────

    private object SetTheme(JsonElement payload)
    {
        var theme = ReadOptionalString(payload, "theme");
        if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)
            || string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
        {
            _state.UiTheme = theme!.ToLowerInvariant();
            _storage.Save(_state);
            return new { saved = true, theme = _state.UiTheme };
        }
        return new { saved = false, message = "theme must be 'dark' or 'light'" };
    }

    private object ExportPowerShell(JsonElement payload)
    {
        var draft = ReadObject(payload, "draft");
        var script = _commandBuilder.BuildPowerShellScript(draft, includeDefaults: false);

        // Stash on the system clipboard so the user can paste anywhere. We
        // also return the script body so the UI can show a confirmation
        // preview / let the user save to file in a future release.
        try
        {
            if (Clipboard.ContainsText() || !string.IsNullOrEmpty(script))
            {
                Clipboard.SetText(script);
            }
        }
        catch (Exception ex)
        {
            MatrixDesktop.Shared.Logger.Warn($"ExportPowerShell clipboard set failed: {ex.Message}");
        }

        return new { script, copied = true };
    }

    private async Task<object?> OpenPreviewAsync()
    {
        if (_previewWindow is { IsDisposed: false })
        {
            // Already open — bring it forward and re-apply the latest query.
            _previewWindow.BringToFront();
            _previewWindow.Activate();
            return new { opened = true, alreadyOpen = true };
        }

        var webRoot = PreviewWindow.FindWebRoot();
        if (webRoot is null)
        {
            MatrixDesktop.Shared.Logger.Warn("Preview requested but no web/ folder was found beside the configurator.");
            return new { opened = false, message = "web/ assets not found beside MatrixDesktopConfigurator.exe (or in dev tree)." };
        }

        var userDataFolder = GetConfiguratorAppDataFolder("PreviewWebView2");
        var preview = new PreviewWindow(webRoot, userDataFolder);
        preview.FormClosed += (_, _) => _previewWindow = null;
        _previewWindow = preview;
        preview.Show();

        // First navigation uses the latest saved draft so the preview matches
        // what's on screen immediately rather than showing defaults briefly.
        var query = _commandBuilder.BuildWebQueryString(_state.LastDraft ?? _commandBuilder.CreateDefaultDraft());
        await preview.NavigateWithQueryAsync(query);
        return new { opened = true, alreadyOpen = false };
    }

    private object ClosePreview()
    {
        if (_previewWindow is { IsDisposed: false })
        {
            try { _previewWindow.Close(); } catch { /* ignore */ }
        }
        _previewWindow = null;
        return new { closed = true };
    }

    private async Task<object?> PreviewCommandAsync(JsonElement payload)
    {
        if (_previewWindow is null || _previewWindow.IsDisposed) return new { applied = false, reason = "no-preview" };
        if (!_previewWindow.IsReady) return new { applied = false, reason = "not-ready" };

        var draft = ReadObject(payload, "draft");
        var query = _commandBuilder.BuildWebQueryString(draft);
        await _previewWindow.NavigateWithQueryAsync(query);
        return new { applied = true };
    }

    private object LoadHelp()
    {
        if (_cachedHelpText is not null)
        {
            return new { text = _cachedHelpText };
        }

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("MatrixDesktopConfigurator.ArgumentGuide.txt");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                _cachedHelpText = reader.ReadToEnd();
                return new { text = _cachedHelpText };
            }
        }
        catch (Exception ex)
        {
            MatrixDesktop.Shared.Logger.Warn($"LoadHelp failed: {ex.Message}");
        }

        return new { text = "Argument guide is not available in this build." };
    }

    private object LoadStatePayload()
    {
        return new
        {
            metadata = _metadata,
            defaultDraft = _commandBuilder.CreateDefaultDraft(),
            state = _state,
            storage = new
            {
                path = _storage.StoragePath,
                portable = _storage.UsesPortablePath,
            },
        };
    }

    private object SaveDraft(JsonElement payload)
    {
        _state.LastDraft = ReadObject(payload, "draft");
        _state.SelectedPresetId = ReadOptionalString(payload, "selectedPresetId");
        _storage.Save(_state);
        return new { saved = true };
    }

    private object SavePreset(JsonElement payload)
    {
        var presetPayload = payload.GetProperty("preset");
        var id = ReadOptionalString(presetPayload, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = Guid.NewGuid().ToString("N");
        }

        var name = ReadOptionalString(presetPayload, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Untitled preset";
        }

        var values = ReadObject(presetPayload, "values");
        var now = DateTimeOffset.UtcNow;
        var existing = _state.UserPresets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new UserPreset
            {
                Id = id,
                CreatedUtc = now,
            };
            _state.UserPresets.Add(existing);
        }

        existing.Name = name;
        existing.Values = values;
        existing.UpdatedUtc = now;
        _state.SelectedPresetId = id;
        _state.LastDraft = StorageService.CloneObject(values);
        _storage.Save(_state);

        return new { preset = existing, state = _state };
    }

    private object DeletePreset(JsonElement payload)
    {
        var id = ReadOptionalString(payload, "id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            _state.UserPresets.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_state.SelectedPresetId, id, StringComparison.OrdinalIgnoreCase))
            {
                _state.SelectedPresetId = null;
            }
        }

        _storage.Save(_state);
        return new { state = _state };
    }

    private object BuildCommand(JsonElement payload)
    {
        var draft = ReadObject(payload, "draft");
        var includeDefaults = ReadBool(payload, "includeDefaults");
        var forTest = ReadBool(payload, "forTest");
        return new
        {
            command = _commandBuilder.BuildCommand(draft, includeDefaults, forTest),
        };
    }

    private object ImportCommand(JsonElement payload)
    {
        var command = ReadOptionalString(payload, "command") ?? string.Empty;
        var result = _argumentImporter.Import(command, _commandBuilder.CreateDefaultDraft());

        _state.SelectedPresetId = null;
        _state.LastDraft = StorageService.CloneObject(result.Draft);
        _storage.Save(_state);

        return new
        {
            draft = result.Draft,
            applied = result.Applied,
            ignored = result.Ignored,
            state = _state,
        };
    }

    private object RandomizeDraft(JsonElement payload)
    {
        var draft = ReadObject(payload, "draft");
        var scope = ReadOptionalString(payload, "scope") ?? "visual";
        var randomized = _randomizer.Randomize(draft, scope);
        return new
        {
            draft = randomized,
            scope,
        };
    }

    private object CopyCommand(JsonElement payload)
    {
        var command = ReadOptionalString(payload, "command") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(command))
        {
            Clipboard.SetText(command);
        }

        return new { copied = true };
    }

    private object TestCommand(JsonElement payload)
    {
        var draft = ReadObject(payload, "draft");
        StopTestProcess();

        var exe = FindMatrixDesktopExe();
        if (exe is null)
        {
            throw new FileNotFoundException("MatrixDesktop.exe was not found beside the configurator or in the local build output.");
        }

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };

        foreach (var argument in _commandBuilder.BuildArguments(draft, includeDefaults: false, forTest: true))
        {
            info.ArgumentList.Add(argument);
        }

        _testProcess = Process.Start(info);
        if (_testProcess is null)
        {
            throw new InvalidOperationException("MatrixDesktop.exe did not start.");
        }

        return new
        {
            processId = _testProcess.Id,
            command = _commandBuilder.BuildCommand(draft, includeDefaults: false, forTest: true),
        };
    }

    private object StopTest()
    {
        StopTestProcess();
        return new { stopped = true };
    }

    private void StopTestProcess()
    {
        var process = _testProcess;
        _testProcess = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            try
            {
                process.CloseMainWindow();
            }
            catch
            {
                // Ignore.
            }

            if (!process.WaitForExit(1500))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task RespondAsync(string id, bool ok, object? payload)
    {
        if (string.IsNullOrWhiteSpace(id) || _webView.CoreWebView2 is null)
        {
            return;
        }

        var response = JsonSerializer.Serialize(new
        {
            id,
            ok,
            payload,
        }, _jsonOptions);

        try
        {
            // Failures in the embedded JS (missing window.configHost, malformed payload,
            // navigation in flight) used to swallow silently — log them so a stuck UI
            // produces a diagnostic instead of vanishing.
            await _webView.CoreWebView2.ExecuteScriptAsync($"window.configHost && window.configHost.receive({response});");
        }
        catch (Exception ex)
        {
            MatrixDesktop.Shared.Logger.Warn($"ExecuteScriptAsync failed for response id='{id}': {ex.Message}");
        }
    }

    private static JsonObject ReadObject(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(propertyName, out var element))
        {
            return JsonNode.Parse(element.GetRawText())?.AsObject() ?? [];
        }

        return [];
    }

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(propertyName, out var element))
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        }

        return null;
    }

    private static bool ReadBool(JsonElement payload, string propertyName)
    {
        return payload.ValueKind == JsonValueKind.Object
               && payload.TryGetProperty(propertyName, out var element)
               && element.ValueKind == JsonValueKind.True;
    }

    private static string GetConfiguratorWebRoot()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "configurator"));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Missing configurator web assets folder: {root}");
        }

        return root;
    }

    private static string GetConfiguratorAppDataFolder(string childFolder)
    {
        // Mirror MainForm.GetWritableAppDataFolder: try LocalAppData, then RoamingAppData,
        // then %TEMP%. The previous implementation only fell back on a missing LOCALAPPDATA
        // env var; it would still throw on a restricted-access LOCALAPPDATA. WebView2 and
        // the storage layer both need a writable directory, so multi-tier fallback matters.
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.GetTempPath(),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;

            var folder = Path.Combine(root, "MatrixDesktop", "Configurator", childFolder);
            try
            {
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch (Exception ex)
            {
                MatrixDesktop.Shared.Logger.Warn($"Configurator AppData root not writable, trying next. Root='{root}' Error='{ex.Message}'.");
            }
        }

        // Last-ditch: a fresh subdirectory under the system temp dir. If even that fails
        // we let the exception propagate — the configurator genuinely cannot run without
        // somewhere to store WebView2 state.
        var lastResort = Path.Combine(Path.GetTempPath(), $"MatrixDesktop-Configurator-{Environment.ProcessId}", childFolder);
        Directory.CreateDirectory(lastResort);
        return lastResort;
    }

    private static string? FindMatrixDesktopExe()
    {
        var publishedCandidate = Path.Combine(AppContext.BaseDirectory, "MatrixDesktop.exe");
        if (File.Exists(publishedCandidate))
        {
            return publishedCandidate;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            var releaseCandidate = Path.Combine(current.FullName, "MatrixDesktop", "bin", "Release", "net10.0-windows", "MatrixDesktop.exe");
            if (File.Exists(releaseCandidate))
            {
                return releaseCandidate;
            }

            var debugCandidate = Path.Combine(current.FullName, "MatrixDesktop", "bin", "Debug", "net10.0-windows", "MatrixDesktop.exe");
            if (File.Exists(debugCandidate))
            {
                return debugCandidate;
            }
        }

        return null;
    }

    private void TryTightenWebViewSettings()
    {
        try
        {
            var settings = _webView.CoreWebView2.Settings;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
        }
        catch
        {
            // Settings availability varies by runtime.
        }
    }

    private static void TryOpenExternal(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        try
        {
            // Capture-and-dispose so we don't retain a Process handle the shell already owns.
            using var p = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MatrixDesktop.Shared.Logger.Warn($"Failed to open external URI '{uri}': {ex.Message}");
        }
    }
}
