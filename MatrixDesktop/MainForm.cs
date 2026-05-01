using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MatrixDesktop;

public sealed class MainForm : Form
{
    private const string HostName = "appassets.local";
    private static readonly string HostOriginPrefix = $"https://{HostName}/";

    private readonly WebView2 _webView;
    private readonly string _startupQueryString;
    private readonly EventHandler _displaySettingsChangedHandler;
    private readonly AppOptions _appOptions;

    // Best-effort foreground enforcement on startup.
    // Windows may refuse focus stealing in some cases, so we retry briefly.
    private System.Windows.Forms.Timer? _foregroundEnforcer;
    private int _foregroundEnforcerTicks;

    // Exit on non-injected keyboard input (low-level keyboard hook).
    private readonly LowLevelKeyboardExit? _keyboardExit;

    // Stored so we can detach on shutdown.
    private EventHandler<CoreWebView2NewWindowRequestedEventArgs>? _newWindowRequestedHandler;
    private EventHandler<CoreWebView2NavigationStartingEventArgs>? _navigationStartingHandler;

    private bool _isShuttingDown;

    private static string? _cachedUserDataFolder;

    public MainForm(string[]? args)
    {
        Text = "Matrix Digital Rain";

        _appOptions = AppCli.Parse(args, out var passthroughArgs);

        // Register a keyboard exit handler that ignores typical software-injected input.
        // (Filters LLKHF_INJECTED / LLKHF_LOWER_IL_INJECTED.)
        try
        {
            _keyboardExit = new LowLevelKeyboardExit(this, _appOptions.ExitOnEsc, _appOptions.ExitOnAnyKey, _appOptions.GlobalKeyExit);
        }
        catch
        {
            _keyboardExit = null;
        }

        ApplyWindowModeAndBounds(initial: true);

        // If monitors are added/removed or resolution changes while the app is running,
        // resize to keep spanning the full virtual desktop.
        _displaySettingsChangedHandler = (_, __) =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() => ApplyWindowModeAndBounds(initial: false)));
            }
            catch
            {
                // Ignore cross-thread / shutdown timing edge cases.
            }
        };

        // Helps the wrapper feel more "native" and avoids a bright flash on startup.
        BackColor = System.Drawing.Color.Black;

        _startupQueryString = MatrixArgs.BuildQueryString(passthroughArgs);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.Black,
            DefaultBackgroundColor = System.Drawing.Color.Black,
            CreationProperties = new CoreWebView2CreationProperties
            {
                // For a portable folder distribution, it is useful to keep the WebView2 profile/cache
                // beside the EXE when possible. If the folder isn't writable, we fall back.
                UserDataFolder = GetBestEffortUserDataFolder(),
            },
        };

        Controls.Add(_webView);
        Load += MainForm_Load;
        Shown += (_, __) =>
        {
            TryApplyCursorVisibility();

            // Some launch contexts (shells, scripts, startup tasks) don't reliably
            // activate the new window. Make a best-effort attempt to become the
            // foreground app.
            TryBeginForegroundEnforce();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        try
        {
            // Physical/non-injected key exit (low-level keyboard hook).
            _keyboardExit?.Install();
        }
        catch
        {
            // Ignore.
        }

        try
        {
            // Only needed for fullscreen / borderless modes.
            if (_appOptions.WindowMode != WindowMode.Windowed)
            {
                SystemEvents.DisplaySettingsChanged += _displaySettingsChangedHandler;
            }
        }
        catch
        {
            // SystemEvents can fail in some restricted environments; ignore.
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        // Uninstall keyboard hook first to prevent callbacks during handle teardown
        try
        {
            _keyboardExit?.Uninstall();
        }
        catch
        {
            // Ignore.
        }

        // Only detach DisplaySettingsChanged if we attached it (non-windowed modes)
        if (_appOptions.WindowMode != WindowMode.Windowed)
        {
            try
            {
                SystemEvents.DisplaySettingsChanged -= _displaySettingsChangedHandler;
            }
            catch
            {
                // Ignore.
            }
        }

        // Ensure foreground enforcer is stopped to prevent timer ticks during disposal
        StopForegroundEnforcer();

        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Stop any startup foreground retry timer before handles start tearing down.
        StopForegroundEnforcer();

        // Best-effort cleanup *before* handles start tearing down.
        TryCleanupWebView();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try
        {
            if (_appOptions.HideCursor)
            {
                Cursor.Show();
            }
        }
        catch
        {
            // Ignore.
        }

        try
        {
            _keyboardExit?.Dispose();
        }
        catch
        {
            // Ignore.
        }

        base.OnFormClosed(e);
    }

    private void ApplyWindowModeAndBounds(bool initial)
    {
        try
        {
            TopMost = _appOptions.TopMost;

            if (_appOptions.WindowMode == WindowMode.Windowed)
            {
                // Windowed: do not force resizing after launch.
                if (initial)
                {
                    FormBorderStyle = FormBorderStyle.Sizable;
                    StartPosition = FormStartPosition.CenterScreen;
                    WindowState = FormWindowState.Normal;

                    // Choose a reasonable default that fits the primary working area.
                    var primaryScreen = Screen.PrimaryScreen;
                    var wa = primaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 720);
                    var w = Math.Min(1280, wa.Width);
                    var h = Math.Min(720, wa.Height);
                    if (w < 640) w = Math.Max(320, wa.Width);
                    if (h < 360) h = Math.Max(240, wa.Height);
                    ClientSize = new System.Drawing.Size(w, h);
                    
                    // If no primary screen, center on virtual screen
                    if (primaryScreen == null)
                    {
                        StartPosition = FormStartPosition.Manual;
                        var vs = SystemInformation.VirtualScreen;
                        Location = new System.Drawing.Point(
                            vs.X + (vs.Width - w) / 2,
                            vs.Y + (vs.Height - h) / 2);
                    }
                }

                return;
            }

            // Borderless fullscreen.
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            WindowState = FormWindowState.Normal;

            var target = GetTargetBounds();
            if (target.Width <= 0 || target.Height <= 0) return;

            Bounds = target;
        }
        catch
        {
            // Ignore.
        }
    }

    private System.Drawing.Rectangle GetTargetBounds()
    {
        if (_appOptions.WindowMode == WindowMode.BorderlessSingleMonitor)
        {
            var screens = Screen.AllScreens;
            var screen = Screen.PrimaryScreen;

            if (_appOptions.MonitorIndex.HasValue)
            {
                var idx = _appOptions.MonitorIndex.Value;
                if (idx >= 0 && idx < screens.Length)
                {
                    screen = screens[idx];
                }
            }

            if (screen is null)
            {
                return SystemInformation.VirtualScreen;
            }

            return _appOptions.UseWorkingArea ? screen.WorkingArea : screen.Bounds;
        }

        // Span all monitors.
        if (!_appOptions.UseWorkingArea)
        {
            // SystemInformation.VirtualScreen accounts for multi-monitor layouts including
            // negative X/Y when a monitor is positioned left/above the primary display.
            return SystemInformation.VirtualScreen;
        }

        // Union of WorkingArea across all monitors (respects taskbars).
        var all = Screen.AllScreens;
        if (all.Length == 0) return SystemInformation.VirtualScreen;

        var rect = all[0].WorkingArea;
        for (var i = 1; i < all.Length; i++)
        {
            rect = System.Drawing.Rectangle.Union(rect, all[i].WorkingArea);
        }
        return rect;
    }

    private void TryApplyCursorVisibility()
    {
        try
        {
            if (_appOptions.HideCursor)
            {
                Cursor.Hide();
            }
            else
            {
                // Don't force-show unless we previously hid it.
            }
        }
        catch
        {
            // Ignore.
        }
    }

    private void TryBeginForegroundEnforce()
    {
        try
        {
            // Ensure we run after the form is fully shown and the message pump is active.
            BeginInvoke(new Action(StartForegroundEnforcer));
        }
        catch
        {
            // Ignore shutdown timing edge cases.
        }
    }

    private void StartForegroundEnforcer()
    {
        try
        {
            StopForegroundEnforcer();

            // First attempt immediately.
            TryEnforceForegroundOnce();

            // Retry briefly: Windows may ignore the first SetForegroundWindow depending on
            // how the process was launched.
            _foregroundEnforcerTicks = 0;
            _foregroundEnforcer = new System.Windows.Forms.Timer { Interval = 200 };
            _foregroundEnforcer.Tick += (_, __) =>
            {
                try
                {
                    if (IsDisposed || Disposing)
                    {
                        StopForegroundEnforcer();
                        return;
                    }

                    if (ForegroundWindow.IsThisProcessForeground())
                    {
                        StopForegroundEnforcer();
                        return;
                    }

                    _foregroundEnforcerTicks++;
                    TryEnforceForegroundOnce();

                    // Stop after ~3 seconds (15 ticks * 200ms).
                    if (_foregroundEnforcerTicks >= 15)
                    {
                        StopForegroundEnforcer();
                    }
                }
                catch
                {
                    StopForegroundEnforcer();
                }
            };

            _foregroundEnforcer.Start();
        }
        catch
        {
            // Ignore.
        }
    }

    private void StopForegroundEnforcer()
    {
        try
        {
            var timer = _foregroundEnforcer;
            _foregroundEnforcer = null; // Clear reference first to prevent race
            if (timer is not null)
            {
                timer.Stop();
                timer.Dispose();
            }
        }
        catch
        {
            // Ignore.
        }
    }

    private void TryEnforceForegroundOnce()
    {
        try
        {
            ForegroundWindow.BestEffortBringToFront(this, _appOptions.TopMost);
        }
        catch
        {
            // Ignore.
        }
    }


    private async void MainForm_Load(object? sender, EventArgs e)
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();

            // Slightly reduce background features we don't need for an offline local app.
            // (This reduces "browser-y" behavior and a bit of overhead.)
            TryTightenWebViewSettings();

            // Open external links (if any) in the system browser.
            _newWindowRequestedHandler = (_, args) =>
            {
                args.Handled = true;
                TryOpenExternal(args.Uri);
            };
            _webView.CoreWebView2.NewWindowRequested += _newWindowRequestedHandler;

            // Prevent unexpected navigation away from our local content.
            _navigationStartingHandler = (_, navArgs) =>
            {
                var uri = navArgs.Uri ?? string.Empty;
                if (string.IsNullOrWhiteSpace(uri)) return;

                // Allow our virtual host origin and a few benign internal schemes.
                if (uri.StartsWith(HostOriginPrefix, StringComparison.OrdinalIgnoreCase)
                    || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                    || uri.StartsWith("edge:", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                navArgs.Cancel = true;
                TryOpenExternal(uri);
            };
            _webView.CoreWebView2.NavigationStarting += _navigationStartingHandler;

            var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
            if (!Directory.Exists(webRoot))
            {
                MessageBox.Show(
                    $"Missing web assets folder:\n{webRoot}\n\n" +
                    "Rebuild/publish the solution and ensure the project copies the 'web' folder to the output directory.",
                    "MatrixDesktop",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                Close();
                return;
            }

            // Map the local 'web' folder to a virtual HTTPS origin.
            // This avoids file:// restrictions while keeping everything local (no HTTP server needed).
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                HostName,
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            var target = string.IsNullOrWhiteSpace(_startupQueryString)
                ? $"{HostOriginPrefix}index.html"
                : $"{HostOriginPrefix}index.html?{_startupQueryString}";

            _webView.CoreWebView2.Navigate(target);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "Microsoft Edge WebView2 Runtime is not installed on this machine.\n\n" +
                "Install the Evergreen WebView2 Runtime and re-run this app.",
                "MatrixDesktop",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "MatrixDesktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void TryCleanupWebView()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        try
        {
            // Detach event handlers so nothing keeps references alive longer than necessary.
            var core = _webView.CoreWebView2;
            if (core is not null)
            {
                if (_newWindowRequestedHandler is not null)
                {
                    try { core.NewWindowRequested -= _newWindowRequestedHandler; } catch { }
                    _newWindowRequestedHandler = null;
                }

                if (_navigationStartingHandler is not null)
                {
                    try { core.NavigationStarting -= _navigationStartingHandler; } catch { }
                    _navigationStartingHandler = null;
                }
            }
        }
        catch
        {
            // Ignore.
        }

        try
        {
            // Disposing WebView2 is the most reliable way to ensure the runtime releases file handles.
            _webView.Dispose();
        }
        catch
        {
            // Ignore.
        }
    }

    private void TryTightenWebViewSettings()
    {
        try
        {
            var settings = _webView.CoreWebView2.Settings;

            // Disable browser conveniences that don't add value for an offline visualizer.
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsZoomControlEnabled = false;

            // Reduce memory overhead for offline visualizer.
            settings.IsScriptEnabled = true; // Required for WebGPU
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;

            // Disable browser accelerator keys (F12, Ctrl+Shift+I, etc.) for cleaner UX.
            settings.AreBrowserAcceleratorKeysEnabled = false;

            // Keep DevTools enabled by default (useful during iteration).
            // If you'd rather lock this down for distribution, set this to false.
            if (_appOptions.DisableDevTools)
            {
                settings.AreDevToolsEnabled = false;
            }
        }
        catch
        {
            // Settings availability varies by WebView2 runtime/version.
        }
    }

    private static void TryOpenExternal(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // Ignore failures to launch external browser.
        }
    }

    private static string GetBestEffortUserDataFolder()
    {
        if (_cachedUserDataFolder is not null)
        {
            return _cachedUserDataFolder;
        }

        // First choice: keep user data beside the EXE (portable-friendly).
        var portable = Path.Combine(AppContext.BaseDirectory, "userdata");
        if (TryEnsureWritableFolder(portable))
        {
            _cachedUserDataFolder = portable;
            return portable;
        }

        // Fallback: LocalAppData (always writable under normal user contexts).
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MatrixDesktop",
            "userdata");
        Directory.CreateDirectory(local);
        _cachedUserDataFolder = local;
        return local;
    }

    private static bool TryEnsureWritableFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
