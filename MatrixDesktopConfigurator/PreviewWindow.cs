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

    // Locates the web/ folder for the embedded preview.
    //
    // First the publish layout, where web/ sits beside the executable. Otherwise walk up
    // looking for the MatrixDesktop project's own web/ folder. The previous version used
    // fixed "..\..\..\.." hops, which encoded an exact output depth and therefore silently
    // returned null for any RID-specific build, where the output gains a win-x64 level.
    public static string? FindWebRoot()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "web");
        if (IsWebRoot(beside))
        {
            return Path.GetFullPath(beside);
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(current.FullName, "MatrixDesktop", "web"),
                         Path.Combine(current.FullName, "web"),
                     })
            {
                if (IsWebRoot(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static bool IsWebRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            var full = Path.GetFullPath(candidate);
            return Directory.Exists(full) && File.Exists(Path.Combine(full, "index.html"));
        }
        catch
        {
            return false;
        }
    }

}
