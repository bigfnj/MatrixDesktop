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
    ];

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
