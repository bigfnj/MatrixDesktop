using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MatrixDesktop;

internal sealed class MatrixApplicationContext : ApplicationContext
{
    private readonly HostLaunchPlan _plan;
    private readonly AppOptions _options;
    private readonly bool _wallpaper;
    private readonly PlaybackPolicy _playback = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 400 };
    private bool _refreshPending;
    private readonly CoordinatorWindow _coordinator;
    private NotifyIcon? _tray;
    private ToolStripMenuItem? _pauseMenu;
    private MainForm? _form;
    private nint _parent;
    private bool _exiting, _recovering, _reconciling, _reconcileRequested, _ready;
    private long _retryAt, _readySince;
    private int _retryCount;
    private readonly RendererFailurePolicy _rendererFailures = new();
    private uint _screensaverInputTick;
    private readonly DesktopPointerBridge? _pointer;
    private WallpaperInstanceGuard? _instance;
    private static readonly TimeSpan TakeoverBound = TimeSpan.FromSeconds(10);

    // True once the context has ended, including inside its own constructor; HostLoop then
    // disposes it instead of starting a message loop nothing could end.
    internal bool ExitRequested => _exiting;

    internal MatrixApplicationContext(HostLaunchPlan plan)
    {
        _plan = plan;
        _options = AppCli.Parse(plan.Arguments, out _);
        _wallpaper = plan.Kind == HostLaunchKind.Standard && _options.WindowMode == WindowMode.Wallpaper;
        _coordinator = new CoordinatorWindow(OnHostMessage);
        if (_wallpaper && _options.WallpaperInput)
            _pointer = new DesktopPointerBridge(pointer => _coordinator.Post(() =>
            {
                if (!_exiting && !_playback.Paused) _form?.PostHostPointer(pointer);
            }));
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // Priority before TryCreateSurface in both branches (WebView2 children inherit the
        // class), and after CreateTray so a refused class can show its balloon.
        if (_wallpaper)
        {
            if (!TakeWallpaperOwnership()) return;
            CreateTray();
            ApplyPriority();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            _timer.Tick += OnTick;
            _timer.Start();
            _refreshTimer.Tick += OnRefreshTimer;
            TryCreateSurface();
        }
        else
        {
            ApplyPriority();
            if (plan.IsPreview || plan.IsScreensaver) { _timer.Interval = 100; _timer.Tick += OnTick; _timer.Start(); }
            TryCreateSurface();
        }
    }

    // The newest wallpaper launch wins: a running one in this session and scope is asked to exit
    // and this launch continues once it has. If it does not hand over in time it keeps the
    // desktop, and this launch says so and ends (HostLoop disposes a context ended here).
    private bool TakeWallpaperOwnership()
    {
        var scope = WallpaperInstanceGuard.Scope(Environment.GetEnvironmentVariable);
        _instance = WallpaperInstanceGuard.Acquire(scope, TakeoverBound);
        if (_instance.Result == WallpaperInstanceGuard.Outcome.TimedOut)
        {
            var message = $"Another MatrixDesktop wallpaper did not exit within {TakeoverBound.TotalSeconds:0} seconds, so this launch stopped. End the running MatrixDesktop in Task Manager, then launch it again.";
            Shared.Logger.Warn(message);
            try { MessageBox.Show(message, "MatrixDesktop", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch { /* the dialog itself may fail in degraded states */ }
            ExitThread();
            return false;
        }
        if (_instance.Result == WallpaperInstanceGuard.Outcome.Unavailable)
        {
            Shared.Logger.Warn($"The wallpaper single-instance guard is unavailable ({_instance.Problem}); this launch cannot replace another running wallpaper.");
            return true;
        }
        if (_instance.Result == WallpaperInstanceGuard.Outcome.TookOver) Shared.Logger.Info($"Took over from the running wallpaper (scope {scope}).");
        _instance.ListenForTakeover(() => _coordinator.Post(() =>
        {
            Shared.Logger.Info("A newer wallpaper launch took over; exiting.");
            ExitThread();
        }));
        return true;
    }

    private void CreateTray()
    {
        var menu = new ContextMenuStrip();
        _pauseMenu = new ToolStripMenuItem("Pause", null, (_, _) =>
        {
            SetPause(PauseReason.Manual, (_playback.Reasons & PauseReason.Manual) == 0);
            _pauseMenu!.Text = (_playback.Reasons & PauseReason.Manual) != 0 ? "Resume" : "Pause";
        });
        menu.Items.Add(_pauseMenu);
        menu.Items.Add("Configure...", null, (_, _) =>
        {
            try { using var process = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "MatrixDesktopConfigurator.exe")) { UseShellExecute = true }); }
            catch (Exception error) { Report(error.Message); }
        });
        var refresh = new ToolStripMenuItem("Refresh desktop", null, async (_, _) => await RefreshResourcesAsync());
        menu.Items.Add(refresh);
        // Only a scene that applies native resources while it runs can be refreshed (AUD-09).
        menu.Opening += (_, _) => refresh.Visible = _form?.NativeResourcesRefreshable == true;
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = AutostartService.Enabled };
        autostart.Click += (_, _) =>
        {
            try { AutostartService.SetEnabled(!AutostartService.Enabled, _plan.Arguments); autostart.Checked = AutostartService.Enabled; }
            catch (Exception error) { Report(error.Message); }
        };
        menu.Items.Add(autostart);
        // The shortcut can change outside this process (the configurator, Explorer, another launch).
        menu.Opening += (_, _) => autostart.Checked = AutostartService.Enabled;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone(),
            Text = "MatrixDesktop wallpaper", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => _pauseMenu.PerformClick();
    }

    private void TryCreateSurface()
    {
        if (_exiting || _form is not null) return;
        try
        {
            _parent = _plan.IsPreview ? _plan.PreviewParent : _wallpaper ? DesktopWindowHost.FindWorker() : 0;
            if ((_wallpaper || _plan.IsPreview) && !DesktopWindowHost.IsUsable(_parent))
                throw new InvalidOperationException(_wallpaper ? "Waiting for the Windows desktop host." : "The screensaver preview window is no longer available.");
            var form = new MainForm(_plan);
            _form = form;
            _ready = false;
            form.FormClosed += OnSurfaceClosed;
            form.RendererReady += (_, _) => { _ready = true; _readySince = Environment.TickCount64; _ = ReconcilePauseAsync(); };
            form.RendererFailed += OnRendererFailed;
            if (_wallpaper || _plan.IsPreview)
            {
                _ = form.Handle;
                DesktopWindowHost.Attach(form.Handle, _parent, SystemInformation.VirtualScreen, _plan.IsPreview);
            }
            form.Show();
            if (_plan.IsScreensaver) _screensaverInputTick = LastInputTick();
            _playback.Set(PauseReason.ShellRecovery, false);
            if (_tray is not null) _tray.Text = "MatrixDesktop wallpaper";
            Shared.Logger.Info($"Renderer host ready: wallpaper={_wallpaper}, preview={_plan.IsPreview}, parentClass={DesktopWindowHost.ClassOf(_parent)}, parent=0x{_parent:X}, child=0x{form.Handle:X}.");
        }
        catch (Exception error)
        {
            Shared.Logger.Warn($"Renderer host could not attach: {error.Message}");
            if (_form is not null) { var old = _form; _form = null; old.FormClosed -= OnSurfaceClosed; old.Dispose(); }
            if (!_wallpaper)
            {
                Report(error.Message);
                // A standard launch has no tray to report through; /s only logs and /p stays silent.
                if (!_exiting && _plan.Kind == HostLaunchKind.Standard)
                {
                    try { MessageBox.Show($"MatrixDesktop failed to start:\n{error.Message}", "MatrixDesktop", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch { /* the dialog itself may fail in degraded states */ }
                }
                ExitThread();
                return;
            }
            _playback.Set(PauseReason.ShellRecovery, true);
            _retryAt = Environment.TickCount64 + Math.Min(10000, 500 * (1 << Math.Min(4, _retryCount++)));
            if (_tray is not null) _tray.Text = "MatrixDesktop: waiting for desktop";
        }
    }

    private void OnSurfaceClosed(object? sender, FormClosedEventArgs e)
    {
        var startupFailed = sender is MainForm { StartupFailed: true };
        _form = null;
        _ready = false;
        if (_exiting || _recovering) return;
        var decision = SurfaceClosePolicy.Decide(_wallpaper, startupFailed, _retryCount);
        if (decision.Exit) { ExitThread(); return; }
        _retryCount++;
        _retryAt = Environment.TickCount64 + decision.RetryAfterMs;
        SetPause(PauseReason.ShellRecovery, true);
    }

    // A failed WebView2 process left the surface blank until an unrelated shell event. A reload
    // stays inside the surface; a lost or exhausted renderer is rebuilt after the same capped
    // backoff as any lost wallpaper surface, and ends every other host.
    private void OnRendererFailed(object? sender, RendererFailureKind kind)
    {
        if (_exiting || sender is not MainForm surface || !ReferenceEquals(surface, _form)) return;
        var action = _rendererFailures.Decide(kind, _wallpaper, Environment.TickCount64);
        if (action == RendererFailureAction.LogOnly) return;
        Shared.Logger.Warn($"Renderer failure {kind}: {action}.");
        _ready = false;
        _pointer?.SetEnabled(false);
        if (action == RendererFailureAction.Reload && surface.ReloadRenderer()) return;
        // A dead browser process can make the close throw; never leave a half-closed surface.
        _form = null;
        surface.FormClosed -= OnSurfaceClosed;
        try { surface.Close(); } catch (Exception error) { Shared.Logger.Warn($"The failed renderer surface did not close cleanly: {error.Message}"); }
        try { surface.Dispose(); } catch (Exception error) { Shared.Logger.Warn($"The failed renderer surface did not dispose cleanly: {error.Message}"); }
        if (action == RendererFailureAction.Exit || !_wallpaper) { ExitThread(); return; }
        _parent = 0;
        _retryAt = Environment.TickCount64 + SurfaceClosePolicy.RetryDelayMs(_retryCount++);
        SetPause(PauseReason.ShellRecovery, true);
    }

    private void RecoverSurface()
    {
        if (_exiting) return;
        _recovering = true;
        try
        {
            var old = _form; _form = null; _ready = false;
            if (old is not null) { old.FormClosed -= OnSurfaceClosed; old.Close(); old.Dispose(); }
            _parent = 0; _retryAt = 0;
        }
        finally { _recovering = false; }
        TryCreateSurface();
    }

    private void OnTick(object? sender, EventArgs args)
    {
        if (_exiting) return;
        if (_plan.IsScreensaver)
        {
            if (LastInputTick() != _screensaverInputTick) ExitThread();
            return;
        }
        if (_plan.IsPreview)
        {
            if (!DesktopWindowHost.IsUsable(_parent)) { ExitThread(); return; }
            if (_form is not null) DesktopWindowHost.Attach(_form.Handle, _parent, SystemInformation.VirtualScreen, preview: true);
            return;
        }
        if (_form is null) { if (Environment.TickCount64 >= _retryAt) TryCreateSurface(); return; }
        if (!DesktopWindowHost.IsUsable(_parent) || DesktopWindowHost.ParentOf(_form.Handle) != _parent) { RecoverSurface(); return; }
        if (_retryCount > 0 && RendererFailurePolicy.StayedHealthy(_ready, _readySince, Environment.TickCount64)) _retryCount = 0;
        SetPause(PauseReason.Fullscreen, _options.WallpaperPauseFullscreen && DesktopWindowHost.ForegroundCoversAnyMonitor(Screen.AllScreens.Select(screen => screen.Bounds)));
        SetPause(PauseReason.Battery, _options.WallpaperPauseBattery && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline);
        _pointer?.SetEnabled(_ready && !_playback.Paused);
        if (_refreshPending && !_playback.Paused) { _refreshPending = false; _ = RefreshResourcesAsync(); }
    }

    // 2026-11-08 (AUD-09): accepted broadcasts come in bursts (a theme change sends several),
    // so each restarts one short timer; a refresh that falls due while paused waits for resume.
    private void OnRefreshTimer(object? sender, EventArgs args)
    {
        _refreshTimer.Stop();
        if (_playback.Paused) _refreshPending = true;
        else _ = RefreshResourcesAsync();
    }

    private void SetPause(PauseReason reason, bool enabled)
    {
        if (enabled) _pointer?.SetEnabled(false);
        if (_playback.Set(reason, enabled)) _ = ReconcilePauseAsync();
    }

    private async Task ReconcilePauseAsync()
    {
        if (!_ready || _form is null || _exiting) return;
        _reconcileRequested = true;
        if (_reconciling) return;
        _reconciling = true;
        try
        {
            while (_reconcileRequested && !_exiting)
            {
                _reconcileRequested = false;
                var applied = _playback.Reasons;
                var surface = _form;
                if (surface is null || surface.IsDisposed || !_ready) return;
                try { await surface.SetHostPausedAsync(applied != PauseReason.None, PlaybackPolicy.SuspendsRenderer(applied), applied.ToString()); }
                catch when (surface.IsDisposed || !ReferenceEquals(surface, _form)) { }
                if (applied != _playback.Reasons || !ReferenceEquals(surface, _form)) _reconcileRequested = true;
            }
        }
        catch (Exception error) { Shared.Logger.Warn($"Could not apply wallpaper playback state: {error.Message}"); }
        finally { _reconciling = false; }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs args) => _coordinator.Post(() =>
    {
        if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect) SetPause(PauseReason.Session, true);
        else if (args.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect) SetPause(PauseReason.Session, false);
    });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs args) => _coordinator.Post(() =>
    {
        if (args.Mode == PowerModes.Suspend) SetPause(PauseReason.Suspend, true);
        else if (args.Mode == PowerModes.Resume) SetPause(PauseReason.Suspend, false);
    });

    private void OnDisplayChanged(object? sender, EventArgs args) => _coordinator.Post(() => { if (_wallpaper) RecoverSurface(); });
    private void OnHostMessage(int message, nint wParam, nint lParam)
    {
        if (_exiting || _coordinator is null) return;
        if (message == 0x0010 || (message == 0x0016 && wParam != 0)) { ExitThread(); return; }
        if (message == 0x0218 && wParam == 0x8013 && lParam != 0)
        {
            var data = Marshal.PtrToStructure<PowerSetting>(lParam);
            if (data.Setting == CoordinatorWindow.ConsoleDisplayState && data.Length >= 4)
                SetPause(PauseReason.DisplayOff, data.Value == 0);
        }
        if (!_wallpaper) return;
        if (message == _coordinator.TaskbarCreated)
        {
            if (_tray is not null) { _tray.Visible = false; _tray.Visible = true; }
            RecoverSurface();
        }
        else if (PlaybackPolicy.ShouldRefreshNativeResources(message, wParam, _form?.NativeResourcesRefreshable == true)) { _refreshTimer.Stop(); _refreshTimer.Start(); }
    }

    private async Task RefreshResourcesAsync()
    {
        try { if (_ready && _form is not null && !_exiting) await _form.RefreshNativeResourcesAsync(); }
        catch (Exception error) { Report($"Desktop resources could not be refreshed: {error.Message}"); }
    }

    private void ApplyPriority()
    {
        try
        {
            var requested = PriorityPolicy.ClassOf(PlaybackPolicy.ResolvePriority(_options.ProcessPriority, _wallpaper));
            using var process = Process.GetCurrentProcess();
            PriorityPolicy.Apply(requested, new ProcessPriorityTarget(process), Report);
        }
        catch (Exception error) { Report($"The requested process priority was not applied: {error.Message}"); }
    }

    private void Report(string message)
    {
        Shared.Logger.Warn(message);
        _tray?.ShowBalloonTip(5000, "MatrixDesktop", message, ToolTipIcon.Warning);
    }

    protected override void ExitThreadCore()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop(); _timer.Dispose();
        _refreshTimer.Stop(); _refreshTimer.Dispose();
        _pointer?.Dispose();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_wallpaper)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        }
        if (_form is not null) { _form.FormClosed -= OnSurfaceClosed; _form.Close(); _form.Dispose(); _form = null; }
        if (_tray is not null)
        {
            _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Icon?.Dispose(); _tray.Dispose(); _tray = null;
        }
        // After the surface and tray are gone, so a waiting newer launch takes an empty desktop.
        _instance?.Dispose(); _instance = null;
        _coordinator.Dispose();
        base.ExitThreadCore();
    }

    [StructLayout(LayoutKind.Sequential)] private struct PowerSetting { public Guid Setting; public uint Length; public uint Value; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Tick; }
    private static uint LastInputTick()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref input) ? input.Tick : 0;
    }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);

    private sealed class CoordinatorWindow : NativeWindow, IDisposable
    {
        internal static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");
        private readonly Action<int, nint, nint> _callback;
        private readonly Queue<Action> _pending = new();
        private nint _powerNotification;
        private bool _disposed;
        internal int TaskbarCreated { get; }
        internal CoordinatorWindow(Action<int, nint, nint> callback)
        {
            _callback = callback;
            CreateHandle(new CreateParams { Caption = "MatrixDesktop coordinator" });
            TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
            var setting = ConsoleDisplayState;
            _powerNotification = RegisterPowerSettingNotification(Handle, ref setting, 0);
        }
        internal void Post(Action action)
        {
            lock (_pending) { if (_disposed) return; _pending.Enqueue(action); }
            PostMessage(Handle, 0x8001, 0, 0);
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x8001)
            {
                while (true)
                {
                    Action action;
                    lock (_pending) { if (_pending.Count == 0) break; action = _pending.Dequeue(); }
                    if (!_disposed) action();
                }
            }
            else _callback(message.Msg, message.WParam, message.LParam);
            if (_disposed) return;
            base.WndProc(ref message);
        }
        public void Dispose()
        {
            lock (_pending) { _disposed = true; _pending.Clear(); }
            if (_powerNotification != 0) { UnregisterPowerSettingNotification(_powerNotification); _powerNotification = 0; }
            DestroyHandle();
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid setting, int flags);
        [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(nint handle);
    }
}
