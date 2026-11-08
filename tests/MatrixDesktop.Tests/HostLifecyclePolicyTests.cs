using System.Text.RegularExpressions;

namespace MatrixDesktop.Tests;

// Host startup, storage and lifecycle decisions that the WinForms host delegates to pure code.
internal static class HostLifecyclePolicyTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Host WebView2 profile record keeps the format the gate parses", ProfileRecordFormat),
        ("Host write probe stays writable while another instance holds its probe open", OverlappingProbes),
        ("Host write probe still reports a write-denied folder as unwritable", DeniedProbe),
        ("Host storage warns when it falls back past the preferred base folder", FallbackWarning),
        ("Host priority reports a class Windows did not grant", PriorityReadBack),
        ("Host pause keeps the frozen frame visible unless lock, sleep or display-off took it away", PauseSuspension),
    ];

    // AUD-06. Hiding and suspending the renderer for every pause put black or the static
    // wallpaper on the desktop for a tray pause, a fullscreen app on another monitor or battery.
    private static void PauseSuspension()
    {
        const PauseReason hiding = PauseReason.Session | PauseReason.Suspend | PauseReason.DisplayOff;
        var flags = Enum.GetValues<PauseReason>().Where(reason => reason != PauseReason.None).ToArray();
        Check.Equal(7, flags.Length, "the subset sweep must cover every pause reason");
        for (var mask = 0; mask < 1 << flags.Length; mask++)
        {
            var reasons = PauseReason.None;
            for (var bit = 0; bit < flags.Length; bit++) if ((mask & (1 << bit)) != 0) reasons |= flags[bit];
            Check.Equal((reasons & hiding) != 0, PlaybackPolicy.SuspendsRenderer(reasons), $"renderer suspension for '{reasons}'");
        }
        foreach (var visible in new[] { PauseReason.Manual, PauseReason.Fullscreen, PauseReason.Battery, PauseReason.ShellRecovery,
                     PauseReason.Manual | PauseReason.Fullscreen | PauseReason.Battery | PauseReason.ShellRecovery })
            Check.False(PlaybackPolicy.SuspendsRenderer(visible), $"a '{visible}' pause must keep the frozen frame on the desktop");
        foreach (var hidden in new[] { PauseReason.Session, PauseReason.Manual | PauseReason.Session, PauseReason.Suspend, PauseReason.DisplayOff })
            Check.True(PlaybackPolicy.SuspendsRenderer(hidden), $"a '{hidden}' pause must still suspend the renderer");
    }

    // Models System.Diagnostics.Process: the setter caches the requested class, Windows
    // grants High for RealTime without the privilege, and only Refresh exposes the grant.
    private sealed class FakePriorityTarget(Func<System.Diagnostics.ProcessPriorityClass, System.Diagnostics.ProcessPriorityClass> grant) : IPriorityTarget
    {
        private System.Diagnostics.ProcessPriorityClass _cached, _os;
        private bool _haveCached;
        internal List<string> Operations { get; } = [];
        public System.Diagnostics.ProcessPriorityClass PriorityClass
        {
            get { Operations.Add("get"); return _haveCached ? _cached : _os; }
            set { Operations.Add("set:" + value); _cached = value; _haveCached = true; _os = grant(value); }
        }
        public void Refresh() { Operations.Add("refresh"); _haveCached = false; }
    }

    private static void PriorityReadBack()
    {
        var unprivileged = (System.Diagnostics.ProcessPriorityClass requested) =>
            requested == System.Diagnostics.ProcessPriorityClass.RealTime ? System.Diagnostics.ProcessPriorityClass.High : requested;
        var reports = new List<string>();
        var target = new FakePriorityTarget(unprivileged);
        PriorityPolicy.Apply(System.Diagnostics.ProcessPriorityClass.RealTime, target, reports.Add);
        Check.Equal("set:RealTime,refresh,get", string.Join(",", target.Operations), "the class must be read back after Refresh, never from the setter's cache");
        Check.Equal(1, reports.Count, "a downgraded RealTime must be reported exactly once");
        Check.Contains(reports[0], "RealTime", "the report must name the requested class");
        Check.Contains(reports[0], "High", "the report must name the class Windows applied");
        foreach (var value in new[] { "idle", "below-normal", "normal", "above-normal", "high" })
        {
            reports.Clear();
            PriorityPolicy.Apply(PriorityPolicy.ClassOf(value), new FakePriorityTarget(unprivileged), reports.Add);
            Check.Equal(0, reports.Count, $"a granted '{value}' class must not be reported");
        }
        Check.Equal(System.Diagnostics.ProcessPriorityClass.RealTime, PriorityPolicy.ClassOf("realtime"), "the CLI value maps to RealTime");

        // Real Process leg on a child this test owns. Unprivileged tokens (the normal case)
        // must see the downgrade reported; a token holding the privilege cannot, and says so.
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            reports.Clear();
            PriorityPolicy.Apply(System.Diagnostics.ProcessPriorityClass.RealTime, new ProcessPriorityTarget(child), reports.Add);
            using var fresh = System.Diagnostics.Process.GetProcessById(child.Id);
            if (fresh.PriorityClass == System.Diagnostics.ProcessPriorityClass.RealTime)
                Console.WriteLine("NOTE  real-process priority leg UNCHECKED: this token was granted RealTime");
            else
                Check.Equal(1, reports.Count, $"a real unprivileged RealTime request (Windows applied {fresh.PriorityClass}) must be reported");
        }
        finally { try { child.Kill(); child.WaitForExit(5000); } catch { } }
    }

    private static string NewTempFolder() => Path.Combine(Path.GetTempPath(), "md-host-probe-" + Guid.NewGuid().ToString("N"));

    private static void OverlappingProbes()
    {
        var folder = NewTempFolder();
        try
        {
            using var firstOpen = new ManualResetEventSlim();
            using var secondOpen = new ManualResetEventSlim();
            string? firstProbe = null, secondProbe = null;
            // Witnessed schedule: the first probe stays open until the second has opened its
            // own, so a legal serialised run cannot make this pass.
            var first = Task.Run(() => HostStorage.TryEnsureWritableFolder(folder, probe =>
            {
                firstProbe = probe;
                firstOpen.Set();
                secondOpen.Wait(TimeSpan.FromSeconds(5));
            }));
            Check.True(firstOpen.Wait(TimeSpan.FromSeconds(10)), "the first probe must reach its open file");
            var second = HostStorage.TryEnsureWritableFolder(folder, probe => { secondProbe = probe; secondOpen.Set(); });
            Check.True(second, "a probe overlapping another instance's open probe must report the folder writable");
            Check.True(secondOpen.IsSet, "the second probe must open its file while the first is still held");
            Check.True(first.Wait(TimeSpan.FromSeconds(10)) && first.Result, "the held probe must also report the folder writable");
            Check.True(firstProbe is not null && secondProbe is not null && firstProbe != secondProbe, "each attempt must own a distinct probe path");
            Check.Equal(0, Directory.GetFiles(folder).Length, "no probe file may remain after both attempts close");

            // Supplemental: unsynchronised probing from two threads never reports unwritable.
            var failures = 0;
            Parallel.For(0, 2, _ => { for (var i = 0; i < 200; i++) if (!HostStorage.TryEnsureWritableFolder(folder)) Interlocked.Increment(ref failures); });
            Check.Equal(0, failures, "concurrent probes of one writable folder must never report it unwritable");
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch { } }
    }

    private static void DeniedProbe()
    {
        var folder = NewTempFolder();
        Directory.CreateDirectory(folder);
        try
        {
            DenyWrites(folder, deny: true);
            Check.False(HostStorage.TryEnsureWritableFolder(folder), "a folder that refuses new files must report unwritable");
        }
        finally
        {
            DenyWrites(folder, deny: false);
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    private static void FallbackWarning()
    {
        var root = NewTempFolder();
        var denied = Path.Combine(root, "denied");
        var fallback = Path.Combine(root, "fallback");
        Directory.CreateDirectory(Path.Combine(denied, "MatrixDesktop", "WebView2"));
        try
        {
            DenyWrites(Path.Combine(denied, "MatrixDesktop", "WebView2"), deny: true);
            var warnings = new List<string>();
            var chosen = HostStorage.SelectWritableFolder([denied, fallback], "WebView2", warnings.Add);
            Check.Equal(Path.Combine(fallback, "MatrixDesktop", "WebView2"), chosen ?? "(null)", "the next writable base must be chosen");
            Check.Equal(1, warnings.Count, "falling back past the preferred base must warn exactly once");
            Check.Contains(warnings[0], fallback, "the warning must name the folder actually used");
            warnings.Clear();
            Check.Equal(Path.Combine(fallback, "MatrixDesktop", "Web"), HostStorage.SelectWritableFolder([fallback, denied], "Web", warnings.Add) ?? "(null)", "the preferred writable base is used");
            Check.Equal(0, warnings.Count, "using the preferred base must not warn");
        }
        finally
        {
            DenyWrites(Path.Combine(denied, "MatrixDesktop", "WebView2"), deny: false);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // Deny creating files in the folder for the current user, so the probe itself is refused.
    private static void DenyWrites(string folder, bool deny)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(folder);
            var acl = info.GetAccessControl();
            var rule = new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.WriteData,
                System.Security.AccessControl.AccessControlType.Deny);
            if (deny) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(folder, deny ? UnixFileMode.UserRead | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void ProfileRecordFormat()
    {
        const string requested = @"C:\Users\Some One\AppData\Local\MatrixDesktop\WebView2";
        const string effective = @"D:\gate run\artifacts\gate\webview-profile";
        var record = HostStorage.ProfileRecord(requested, effective);
        // The gate-ci contract: 'WebView2 profile requested=<path> effective=<path>'.
        var match = Regex.Match(record, "^WebView2 profile requested=(?<requested>.*) effective=(?<effective>.*)$");
        Check.True(match.Success, $"the record must match the gate's pattern: {record}");
        Check.Equal(requested, match.Groups["requested"].Value, "the requested folder must round-trip, spaces included");
        Check.Equal(effective, match.Groups["effective"].Value, "the effective folder must round-trip, spaces included");
    }
}
