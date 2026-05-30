using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MatrixDesktop.Shared;

namespace MatrixDesktopConfigurator;

// A separate top-level WebView2 window that the configurator drives to show a
// live preview of the matrix rain with the current draft applied. Designed to
// stay open while the user edits — the configurator pushes URL changes via
// NavigateWithQueryAsync as the draft changes (debounced JS-side at 250ms).
//
// Owned by ConfiguratorForm: it is created on demand, closed implicitly when
// the configurator exits, and tracked so a second "open preview" request just
// brings the existing window to the foreground rather than spawning a duplicate.
internal sealed class PreviewWindow : Form
{
    private const string HostName = "matrix-preview.local";
    private const string HostOriginPrefix = "https://matrix-preview.local/";

    private readonly WebView2 _webView;
    private readonly string _webRoot;
    private readonly string _userDataFolder;
    private string _lastAppliedQuery = string.Empty;
    private bool _initialized;
    private bool _shuttingDown;

    public PreviewWindow(string webRoot, string userDataFolder)
    {
        _webRoot = webRoot;
        _userDataFolder = userDataFolder;

        Text = "MatrixDesktop — Live Preview";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1280;
        Height = 720;
        MinimumSize = new System.Drawing.Size(640, 360);
        BackColor = System.Drawing.Color.Black;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = System.Drawing.Color.Black,
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = _userDataFolder,
            },
        };

        Controls.Add(_webView);
        Shown += async (_, _) => await InitializeAsync();
    }

    public bool IsReady => _initialized && !_shuttingDown && _webView.CoreWebView2 is not null;

    private async Task InitializeAsync()
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();

            // Lock the preview down: no devtools, no context menu, no audio.
            var settings = _webView.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                HostName,
                _webRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            _initialized = true;

            // Initial navigation — empty query string applies all defaults.
            await NavigateWithQueryAsync(string.Empty);
        }
        catch (Exception ex)
        {
            Logger.Error("PreviewWindow initialization failed; closing preview.", ex);
            try
            {
                MessageBox.Show(
                    $"Live preview could not be initialised:\n{ex.Message}",
                    "MatrixDesktop Configurator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch { /* ignore */ }
            try { Close(); } catch { /* ignore */ }
        }
    }

    public async Task NavigateWithQueryAsync(string queryString)
    {
        if (_shuttingDown) return;
        if (!_initialized) return;
        if (_webView.CoreWebView2 is null) return;

        // The query string is built by CommandBuilder.BuildWebQueryString
        // and already starts with '?' (or is empty). Compare against the last
        // applied value so a debounce-triggered re-fire with unchanged values
        // is a no-op.
        var normalized = queryString ?? string.Empty;
        if (string.Equals(_lastAppliedQuery, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _lastAppliedQuery = normalized;

        try
        {
            var url = HostOriginPrefix + "index.html" + normalized;
            _webView.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            Logger.Warn($"PreviewWindow.NavigateWithQueryAsync failed: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _shuttingDown = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { _webView.Dispose(); } catch { /* ignore */ }
        base.OnFormClosed(e);
    }

    // Locate the web/ folder using the same resilience pattern MainForm uses.
    // Tries: 1) sibling to the EXE (publish layout), 2) the dev-side
    // MatrixDesktop project folder (bin/Debug layout). Returns null if neither
    // exists — caller is responsible for showing an error message.
    public static string? FindWebRoot()
    {
        string?[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "web"),
            Path.Combine(AppContext.BaseDirectory, "..", "MatrixDesktop", "web"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MatrixDesktop", "web"),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var full = Path.GetFullPath(candidate);
                if (Directory.Exists(full) && File.Exists(Path.Combine(full, "index.html")))
                {
                    return full;
                }
            }
            catch { /* try the next one */ }
        }

        return null;
    }
}
