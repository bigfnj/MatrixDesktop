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
    private readonly CoordinatorWindow _coordinator;
    private NotifyIcon? _tray;
    private ToolStripMenuItem? _pauseMenu;
    private MainForm? _form;
    private nint _parent;
    private bool _exiting, _recovering, _reconciling, _reconcileRequested, _ready;
    private long _retryAt;
    private int _retryCount;
    private uint _screensaverInputTick;
    private readonly DesktopPointerBridge? _pointer;

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
        ApplyPriority();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        if (_wallpaper)
        {
            CreateTray();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            _timer.Tick += OnTick;
            _timer.Start();
            TryCreateSurface();
        }
        else
        {
            if (plan.IsPreview || plan.IsScreensaver) { _timer.Interval = 100; _timer.Tick += OnTick; _timer.Start(); }
            TryCreateSurface();
        }
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
        menu.Items.Add("Refresh desktop", null, async (_, _) => await RefreshResourcesAsync());
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = AutostartService.Enabled };
        autostart.Click += (_, _) =>
        {
            try { AutostartService.SetEnabled(!AutostartService.Enabled, _plan.Arguments); autostart.Checked = AutostartService.Enabled; }
            catch (Exception error) { Report(error.Message); }
        };
        menu.Items.Add(autostart);
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
            form.RendererReady += (_, _) => { _ready = true; _ = ReconcilePauseAsync(); };
            if (_wallpaper || _plan.IsPreview)
            {
                _ = form.Handle;
                DesktopWindowHost.Attach(form.Handle, _parent, SystemInformation.VirtualScreen, _plan.IsPreview);
            }
            form.Show();
            if (_plan.IsScreensaver) _screensaverInputTick = LastInputTick();
            _retryCount = 0;
            _playback.Set(PauseReason.ShellRecovery, false);
            if (_tray is not null) _tray.Text = "MatrixDesktop wallpaper";
            Shared.Logger.Info($"Renderer host ready: wallpaper={_wallpaper}, preview={_plan.IsPreview}, parentClass={DesktopWindowHost.ClassOf(_parent)}, parent=0x{_parent:X}, child=0x{form.Handle:X}.");
        }
        catch (Exception error)
        {
            Shared.Logger.Warn($"Renderer host could not attach: {error.Message}");
            if (_form is not null) { var old = _form; _form = null; old.FormClosed -= OnSurfaceClosed; old.Dispose(); }
            if (!_wallpaper) { Report(error.Message); ExitThread(); return; }
            _playback.Set(PauseReason.ShellRecovery, true);
            _retryAt = Environment.TickCount64 + Math.Min(10000, 500 * (1 << Math.Min(4, _retryCount++)));
            if (_tray is not null) _tray.Text = "MatrixDesktop: waiting for desktop";
        }
    }

    private void OnSurfaceClosed(object? sender, FormClosedEventArgs e)
    {
        _form = null;
        _ready = false;
        if (_exiting || _recovering) return;
        if (_wallpaper) { _retryAt = Environment.TickCount64 + 500; SetPause(PauseReason.ShellRecovery, true); }
        else ExitThread();
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
        SetPause(PauseReason.Fullscreen, _options.WallpaperPauseFullscreen && DesktopWindowHost.ForegroundCoversAnyMonitor(Screen.AllScreens.Select(screen => screen.Bounds)));
        SetPause(PauseReason.Battery, _options.WallpaperPauseBattery && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline);
        _pointer?.SetEnabled(_ready && !_playback.Paused);
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
                try { await surface.SetHostPausedAsync(applied != PauseReason.None, applied.ToString()); }
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
        else if (message is 0x001a or 0x031a) _ = RefreshResourcesAsync();
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
            var value = PlaybackPolicy.ResolvePriority(_options.ProcessPriority, _wallpaper);
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = value switch
            {
                "idle" => ProcessPriorityClass.Idle, "below-normal" => ProcessPriorityClass.BelowNormal,
                "above-normal" => ProcessPriorityClass.AboveNormal, "high" => ProcessPriorityClass.High,
                "realtime" => ProcessPriorityClass.RealTime, _ => ProcessPriorityClass.Normal,
            };
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
