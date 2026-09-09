using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MatrixDesktop;

public sealed class MainForm : Form
{
    private const string HostName = "appassets.local";
    private static readonly string HostOriginPrefix = $"https://{HostName}/";
    private const int CatastrophicFailureHResult = unchecked((int)0x8000FFFF);

    private WebView2 _webView;
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
    private bool _webViewInitializationStarted;
    private Shared.AppWindowIcon? _windowIcon;

    private static string? _cachedUserDataFolder;

    public MainForm(string[]? args)
    {
        Text = "Matrix Digital Rain";
        TryApplyWindowIcon();

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
        // Routed through the same helper as the session and power handlers. It kept its own
        // inline BeginInvoke, which checked only IsDisposed and swallowed the failure with no
        // log, so a display change racing the close vanished silently: the exact failure mode
        // the marshalling work was meant to remove.
        _displaySettingsChangedHandler = (_, __) =>
            MarshalToUiThread(() => ApplyWindowModeAndBounds(initial: false), "display settings changed");

        // Helps the wrapper feel more "native" and avoids a bright flash on startup.
        BackColor = System.Drawing.Color.Black;

        _startupQueryString = MatrixArgs.BuildQueryString(passthroughArgs);

        _webView = CreateWebView(GetAppDataUserDataFolder());
        Controls.Add(_webView);
        Shown += MainForm_Shown;
    }

    private void TryApplyWindowIcon()
    {
        try
        {
            _windowIcon ??= Shared.AppWindowIcon.Load(typeof(MainForm).Assembly);
            _windowIcon.ApplyTo(this);
        }
        catch
        {
            // The EXE icon is cosmetic; keep startup resilient.
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

        // v1.0: pause the WebView when the workstation locks or the machine
        // sleeps. This stops the GL animation loop + audio + timers, which
        // matters on laptops (battery) and any system where the user expects
        // the rain not to keep rendering while they're away.
        //
        // The guard is not just tidiness. Nothing prevented a second subscription if the
        // handle were ever recreated, and only one unsubscribe happens on teardown, so the
        // surplus subscription would keep this form alive and fire against a disposed
        // WebView.
        // Tracked per event, not as one flag for both. A single flag set after two
        // subscriptions describes neither: if the first += succeeded and the second threw,
        // the flag stayed false, the catch swallowed it, and the successful subscription was
        // never removed. SystemEvents is static and holds a strong reference, so that rooted
        // the form for the life of the process and kept firing against a disposed WebView.
        if (!_sessionSwitchAttached)
        {
            try
            {
                SystemEvents.SessionSwitch += OnSessionSwitch;
                _sessionSwitchAttached = true;
            }
            catch
            {
                // SystemEvents subscription can fail in services / sandboxed environments.
            }
        }

        if (!_powerModeAttached)
        {
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                _powerModeAttached = true;
            }
            catch
            {
                // Not fatal; the session handler above may still be live.
            }
        }
    }

    private bool _sessionSwitchAttached;
    private bool _powerModeAttached;
    private bool _isSuspended;

    // _isSuspended alone cannot serialise these: it is assigned only AFTER the await, and
    // the await hands the UI thread back to the pump. Windows really does deliver two
    // suspend-class events together (PowerModes.Suspend plus SessionLock on sleep-with-lock,
    // RemoteDisconnect plus SessionLock on an RDP drop), so without an in-flight flag the
    // second action would hide the WebView, race the first, and could finish by restoring
    // visibility while the first one reports a successful suspend. That leaves a visible
    // window over a suspended renderer, with _isSuspended true so nothing retries.
    private bool _suspendInFlight;

    // SystemEvents raises its events on a private thread it owns, NOT on the UI thread.
    // Measured on this machine: UI thread id 2, handler thread id 4. So every one of these
    // handlers must marshal before it touches the form or the WebView, exactly as
    // _displaySettingsChangedHandler above already does with BeginInvoke.
    //
    // Before this fix the handlers called _webView.Visible and CoreWebView2.TrySuspendAsync
    // straight from that thread. With Control.CheckForIllegalCrossThreadCalls off, which is
    // the default when no debugger is attached, the write silently proceeded as an
    // unsynchronised cross-thread access to an apartment-bound COM object. With the check
    // on it threw InvalidOperationException, which the handler's own catch swallowed as a
    // WARN. Either way the feature did not work and said nothing.
    private void MarshalToUiThread(Action action, string reason)
    {
        try
        {
            if (IsDisposed || Disposing || !IsHandleCreated)
            {
                return;
            }

            BeginInvoke(action);
        }
        catch (Exception ex)
        {
            // Shutdown races are expected here: the handle can go away between the check
            // and the post. Anything else is worth a line.
            Shared.Logger.Warn($"Could not marshal '{reason}' to the UI thread: {ex.Message}");
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                MarshalToUiThread(async void () => await TrySuspendWebViewAsync($"session {e.Reason}"), $"session {e.Reason}");
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                MarshalToUiThread(() => TryResumeWebView($"session {e.Reason}"), $"session {e.Reason}");
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                MarshalToUiThread(async void () => await TrySuspendWebViewAsync("power suspend"), "power suspend");
                break;
            case PowerModes.Resume:
                MarshalToUiThread(() => TryResumeWebView("power resume"), "power resume");
                break;
        }
    }

    private async Task TrySuspendWebViewAsync(string reason)
    {
        if (_isSuspended) return;
        if (_isShuttingDown) return;
        if (_suspendInFlight) return;

        _suspendInFlight = true;
        try
        {
            if (_webView.CoreWebView2 is null) return;

            // TrySuspendAsync requires the WebView to be hidden first
            // (per https://learn.microsoft.com/microsoft-edge/webview2/concepts/process-model#sleeping-renderer-process).
            // Hiding alone already pauses rendering; the explicit Suspend
            // call releases additional renderer resources.
            _webView.Visible = false;
            var ok = await _webView.CoreWebView2.TrySuspendAsync();
            _isSuspended = ok;

            if (!ok)
            {
                // Hiding is the precondition for suspending, so a failed suspend would
                // otherwise leave a permanently blank window with _isSuspended false,
                // meaning nothing would ever restore it until the next resume event.
                _webView.Visible = true;
                Shared.Logger.Warn($"TrySuspendAsync declined (reason='{reason}'); restored visibility.");
                return;
            }

            Shared.Logger.Info($"WebView suspended (reason='{reason}').");
        }
        catch (Exception ex)
        {
            Shared.Logger.Warn($"Failed to suspend WebView (reason='{reason}'): {ex.Message}");
        }
        finally
        {
            _suspendInFlight = false;
        }
    }

    private void TryResumeWebView(string reason)
    {
        // Guarded like the suspend side. The handlers are detached in OnHandleDestroyed,
        // which runs AFTER OnFormClosing has already disposed the WebView, so a session
        // unlock arriving in that window would otherwise touch a disposed COM object.
        if (_isShuttingDown) return;

        try
        {
            if (_webView.CoreWebView2 is not null)
            {
                _webView.CoreWebView2.Resume();
            }
            _webView.Visible = true;
            _isSuspended = false;
            Shared.Logger.Info($"WebView resumed (reason='{reason}').");
        }
        catch (Exception ex)
        {
            Shared.Logger.Warn($"Failed to resume WebView (reason='{reason}'): {ex.Message}");
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

        // Unhook the v1.0 power/session listeners so they don't fire against
        // a destroyed form's WebView.
        if (_sessionSwitchAttached)
        {
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { /* ignore */ }
            _sessionSwitchAttached = false;
        }

        if (_powerModeAttached)
        {
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { /* ignore */ }
            _powerModeAttached = false;
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

    private static WebView2 CreateWebView(string userDataFolder)
    {
        return new WebView2
        {
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.Black,
            DefaultBackgroundColor = System.Drawing.Color.Black,
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = userDataFolder,
            },
        };
    }

    private async void MainForm_Shown(object? sender, EventArgs e)
    {
        // async void event handlers must never let an exception escape — doing so
        // crashes the entire process via WindowsFormsSynchronizationContext rather
        // than going through OnUnhandledException. InitializeWebViewAsync has its
        // own internal try/catch, but the pre-init helpers (icon application,
        // cursor visibility, foreground enforcement) can also throw and the async
        // state machine would surface those as process-level UnhandledException.
        try
        {
            TryApplyNativeWindowIcons();
            TryApplyCursorVisibility();

            // Some launch contexts (shells, scripts, startup tasks) don't reliably
            // activate the new window. Make a best-effort attempt to become the
            // foreground app.
            TryBeginForegroundEnforce();

            await InitializeWebViewAsync();
        }
        catch (Exception ex)
        {
            Shared.Logger.Error("MainForm_Shown failed; closing form.", ex);
            try
            {
                MessageBox.Show(
                    $"MatrixDesktop failed to start:\n{ex.Message}",
                    "MatrixDesktop",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch { /* dialog itself may fail in degraded states */ }
            try { Close(); } catch { /* form may already be closing */ }
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webViewInitializationStarted || _isShuttingDown || IsDisposed)
        {
            return;
        }

        _webViewInitializationStarted = true;

        try
        {
            await InitializeCurrentWebViewAsync();
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
        catch (COMException ex) when (ex.HResult == CatastrophicFailureHResult)
        {
            await RetryWebViewInitializationAfterCatastrophicFailureAsync(ex);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "MatrixDesktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task InitializeCurrentWebViewAsync()
    {
        if (!IsHandleCreated)
        {
            _ = Handle;
        }

        if (!_webView.IsHandleCreated)
        {
            _ = _webView.Handle;
        }

        await Task.Yield();
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

        string webRoot;
        try
        {
            webRoot = PrepareWebRoot();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Unable to prepare bundled web assets for WebView2.\n\n" +
                $"Source folder:\n{Path.Combine(AppContext.BaseDirectory, "web")}\n\n" +
                $"Error:\n{ex}",
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

    private async Task RetryWebViewInitializationAfterCatastrophicFailureAsync(COMException firstException)
    {
        var originalProfile = _webView.CreationProperties?.UserDataFolder ?? "(unknown)";
        var retryProfile = GetFreshLocalUserDataFolder();

        try
        {
            ReplaceWebView(retryProfile);
            await InitializeCurrentWebViewAsync();
        }
        catch (Exception retryException)
        {
            MessageBox.Show(
                "WebView2 failed to initialize and a retry with a fresh local profile also failed.\n\n" +
                $"Original profile:\n{originalProfile}\n\n" +
                $"Retry profile:\n{retryProfile}\n\n" +
                $"First failure:\n{firstException}\n\n" +
                $"Retry failure:\n{retryException}",
                "MatrixDesktop",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }

    private void ReplaceWebView(string userDataFolder)
    {
        var oldWebView = _webView;

        DetachWebViewHandlers(oldWebView);
        Controls.Remove(oldWebView);

        try
        {
            oldWebView.Dispose();
        }
        catch
        {
            // Ignore.
        }

        _webView = CreateWebView(userDataFolder);
        Controls.Add(_webView);
        _webView.BringToFront();
    }

    private void TryCleanupWebView()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        try
        {
            // Detach event handlers so nothing keeps references alive longer than necessary.
            DetachWebViewHandlers(_webView);
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

    private void DetachWebViewHandlers(WebView2 webView)
    {
        try
        {
            var core = webView.CoreWebView2;
            if (core is null) return;

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
            // Capture-and-dispose the returned Process so its handle is released
            // immediately. With UseShellExecute=true the shell owns the actual
            // process lifecycle; the returned wrapper here only holds an OS handle
            // we don't need.
            using var p = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Shared.Logger.Warn($"TryOpenExternal failed for '{uri}': {ex.Message}");
        }
    }

    private static string GetAppDataUserDataFolder()
    {
        if (_cachedUserDataFolder is not null)
        {
            return _cachedUserDataFolder;
        }

        // WebView2 cannot reliably create profiles beside the EXE when launched from
        // network-like locations such as WSL's Z:\ mount. Keep Chromium state in AppData.
        _cachedUserDataFolder = GetWritableAppDataFolder("WebView2");
        return _cachedUserDataFolder;
    }

    private const int MaxRecoveryProfilesRetained = 3;

    private static string GetFreshLocalUserDataFolder()
    {
        var root = GetWritableAppDataFolder("WebView2Recovery");

        // Prune first. Each of these is a full WebView2 profile, several MB and growing with
        // its cache, and the failure that creates one is usually environmental, so it repeats
        // on every launch: a machine with a broken Edge install accumulated one per start with
        // nothing to reclaim them. Dumps and the log are capped; this was not.
        try
        {
            var stale = new DirectoryInfo(root)
                .GetDirectories()
                .OrderByDescending(static d => d.LastWriteTimeUtc)
                .Skip(MaxRecoveryProfilesRetained - 1)
                .ToArray();

            foreach (var directory in stale)
            {
                try
                {
                    directory.Delete(recursive: true);
                }
                catch
                {
                    // A profile still locked by a live WebView2 is not worth failing over.
                }
            }

            if (stale.Length > 0)
            {
                Shared.Logger.Info($"Pruned {stale.Length} stale WebView2 recovery profile(s) from '{root}'.");
            }
        }
        catch (Exception ex)
        {
            Shared.Logger.Warn($"Could not prune WebView2 recovery profiles: {ex.Message}");
        }

        var folder = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Environment.ProcessId}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string PrepareWebRoot()
    {
        var bundledWebRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "web"));
        if (!Directory.Exists(bundledWebRoot))
        {
            throw new DirectoryNotFoundException(
                $"Missing web assets folder. Rebuild/publish the solution and ensure the project copies the 'web' folder to the output directory: {bundledWebRoot}");
        }

        var stagedWebRoot = Path.GetFullPath(GetWritableAppDataFolder("Web"));
        if (PathsEqual(bundledWebRoot, stagedWebRoot))
        {
            return bundledWebRoot;
        }

        CopyDirectoryIfChanged(bundledWebRoot, stagedWebRoot);
        return stagedWebRoot;
    }

    private static string GetWritableAppDataFolder(string childFolder)
    {
        foreach (var basePath in GetAppDataBasePaths())
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                continue;
            }

            var path = Path.Combine(basePath, "MatrixDesktop", childFolder);
            if (TryEnsureWritableFolder(path))
            {
                return path;
            }
        }

        throw new IOException("No writable AppData or temp folder was available.");
    }

    private static string[] GetAppDataBasePaths()
    {
        return
        [
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.GetTempPath(),
        ];
    }

    // Mirrors the bundled web assets into the staging folder, copying only what actually
    // differs and deleting what is no longer bundled.
    //
    // This used to copy every file unconditionally with overwrite:true despite its name,
    // which cost 2.4 MB of writes and a measured 132 to 211 ms on every single launch
    // against 51 ms for the same work when gated on size and timestamp.
    //
    // The pruning half is not a nice-to-have. The staging folder lives at a fixed path
    // under LocalApplicationData and is shared by every build on the machine, so without
    // it a file that has been renamed or deleted upstream lingers there forever. A stale
    // leftover whose timestamp happens to be newer than the bundled tree would then shadow
    // a fresh build, which is a worse failure than the unconditional copy this replaces.
    private static void CopyDirectoryIfChanged(string sourceRoot, string targetRoot)
    {
        Directory.CreateDirectory(targetRoot);

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            expected.Add(relativePath);

            var targetFile = Path.Combine(targetRoot, relativePath);
            var targetDirectory = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            var source = new FileInfo(sourceFile);
            var target = new FileInfo(targetFile);

            // Length plus last-write time. A content hash would be more precise and would
            // cost a full read of every file, which is the very thing being avoided.
            if (target.Exists
                && target.Length == source.Length
                && target.LastWriteTimeUtc == source.LastWriteTimeUtc)
            {
                continue;
            }

            File.Copy(sourceFile, targetFile, overwrite: true);
            File.SetLastWriteTimeUtc(targetFile, source.LastWriteTimeUtc);
        }

        PruneStaleStagedFiles(targetRoot, expected);
    }

    private static void PruneStaleStagedFiles(string targetRoot, HashSet<string> expected)
    {
        try
        {
            foreach (var stagedFile in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(targetRoot, stagedFile);
                if (expected.Contains(relativePath))
                {
                    continue;
                }

                try
                {
                    File.Delete(stagedFile);
                    Shared.Logger.Info($"Removed stale staged web asset '{relativePath}'.");
                }
                catch (Exception ex)
                {
                    // A locked leftover is not worth failing startup over, but it is worth
                    // knowing about, because it is exactly what would shadow a new build.
                    Shared.Logger.Warn($"Could not remove stale staged web asset '{relativePath}': {ex.Message}");
                }
            }

            // Directories are removed only when empty, deepest first, so an unexpected
            // subtree cannot take a live one with it.
            foreach (var directory in Directory.EnumerateDirectories(targetRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(static path => path.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }
                catch
                {
                    // Best effort.
                }
            }
        }
        catch (Exception ex)
        {
            Shared.Logger.Warn($"Pruning the staged web folder failed: {ex.Message}");
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
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
