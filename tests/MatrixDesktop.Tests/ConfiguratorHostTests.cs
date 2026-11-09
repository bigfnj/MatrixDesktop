using System.Diagnostics;
using MatrixDesktopConfigurator;

namespace MatrixDesktop.Tests;

// Configurator host behaviour that does not need a window: the single-instance guard.
internal static class ConfiguratorHostTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("A second configurator on the same store is refused", SecondConfiguratorRefused),
        ("The desktop snapshot help says wallpaper hosts use the wallpaper only", DesktopSnapshotHelp),
        ("Stop Test ends the Test host but not the processes it started", StopTestEndsOnlyTheHost),
        ("A Test host that ends by itself sends one exit notice for the current test only", TestExitNotice),
        ("Every request the page posts is one the configurator host serves, and Rename keeps the saved values", PageRequestsAreServed),
    ];

    // R2-13. No test project compiles ConfiguratorForm, so a deleted request arm passed every
    // suite while each Rename on the page failed with "Unknown configurator request". The form
    // routes every request through ConfiguratorRequests.DispatchAsync, which this sends each
    // requestHost name in app.js through (the window's own requests reach a stand-in): none may
    // be refused, and renamePreset, answered from the store alone, must keep the saved values,
    // the working draft and the selection on a fixture store. Killing mutations: delete the
    // renamePreset arm, or drop a name from FormTypes; this case fails naming it.
    private static void PageRequestsAreServed()
    {
        using var resource = typeof(ConfiguratorHostTests).Assembly.GetManifestResourceStream("app.js.txt")
            ?? throw new InvalidOperationException("configurator/js/app.js is not embedded in the harness.");
        var source = new StreamReader(resource).ReadToEnd();
        var literal = System.Text.RegularExpressions.Regex.Matches(source, @"requestHost\(\s*[""'`]([A-Za-z]+)[""'`]");
        Check.Equal(System.Text.RegularExpressions.Regex.Matches(source, @"requestHost\(").Count, literal.Count,
            "every requestHost call in app.js must name its request literally, or this contract cannot see it");
        var names = literal.Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        Check.True(names.Count >= 15 && names.Contains("renamePreset"), "app.js posts too few requests for this to be the page: " + string.Join(", ", names));

        var directory = Path.Combine(AppContext.BaseDirectory, "synthetic-requests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storage = new StorageService(Path.Combine(directory, "presets.json"));
        try
        {
            var state = new ConfiguratorState { SelectedPresetId = "night", LastDraft = new System.Text.Json.Nodes.JsonObject { ["effect"] = "stripes" } };
            state.UserPresets.Add(new UserPreset { Id = "night", Name = "Night", Values = new System.Text.Json.Nodes.JsonObject { ["effect"] = "palette" } });
            using var payload = System.Text.Json.JsonDocument.Parse("""{"id":"night","name":"Midnight"}""");
            var refused = new List<string>();
            var byForm = new List<string>();
            foreach (var name in names)
            {
                try
                {
                    ConfiguratorRequests.DispatchAsync(name, payload.RootElement, state, storage,
                        (type, _) => { byForm.Add(type); return Task.FromResult<object?>(null); }).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException error) when (error.Message.StartsWith("Unknown configurator request", StringComparison.Ordinal))
                {
                    refused.Add(name);
                }
            }
            Check.True(refused.Count == 0, "the configurator host refuses requests the page posts: " + string.Join(", ", refused));
            Check.False(byForm.Contains("renamePreset"), "renamePreset is answered from the store, not by the window");
            var stored = storage.Load();
            var preset = stored.UserPresets.Single();
            Check.Equal("Midnight", preset.Name, "Rename through the host's dispatch must store the new name");
            Check.Equal("palette", preset.Values["effect"]!.GetValue<string>(), "Rename must keep the preset's saved values");
            Check.Equal("stripes", stored.LastDraft["effect"]!.GetValue<string>(), "Rename must keep the working draft");
            Check.Equal("night", stored.SelectedPresetId ?? "(none)", "Rename must keep the selection");
        }
        finally
        {
            foreach (var path in new[] { storage.StoragePath, storage.BackupPath, storage.StoragePath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static void TestExitNotice()
    {
        // AUD-43. Killing mutations: drop EnableRaisingEvents in WatchExit (no notice arrives);
        // drop the identity check in ClaimExited (a replaced host's notice clears the current one).
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--stand-in-test-host-child");
        using var host = Process.Start(info)!;
        var pid = host.Id;
        using var noticed = new ManualResetEventSlim();
        (Process? Process, int Pid) notice = (null, 0);
        TestProcessStop.WatchExit(host, (process, id) => { notice = (process, id); noticed.Set(); });
        try { host.Kill(); } catch { }
        Check.True(noticed.Wait(10_000), "a Test host that ends by itself must produce an exit notice, or Stop Test stays enabled");
        Check.True(ReferenceEquals(host, notice.Process) && notice.Pid == pid, "the notice names that process and its pid");

        using var replaced = Process.GetCurrentProcess();
        using var running = Process.GetCurrentProcess();
        Process? current = running;
        Check.False(TestProcessStop.ClaimExited(ref current, replaced), "a replaced Test host's exit notice is not for the running test");
        Check.True(ReferenceEquals(current, running), "and must not clear it, or Stop could no longer end it");
        Check.True(TestProcessStop.ClaimExited(ref current, running) && current is null, "the running Test host's own notice clears it");
    }

    private static void StopTestEndsOnlyTheHost()
    {
        // AUD-74. Killing mutations: kill(true) in TestProcessStop.Run fails the call order;
        // Kill(true) in the Process wrapper fails the real child below.
        var calls = new List<string>();
        TestProcessStop.Run(() => { calls.Add("exited?"); return false; }, () => calls.Add("close"),
            ms => { calls.Add("wait " + ms); return false; }, tree => calls.Add("kill tree=" + tree));
        Check.Equal("exited? close wait 1500 kill tree=False wait 1500", string.Join(" ", calls),
            "Stop asks the window to close, waits, and only then ends the Test host alone");
        calls.Clear();
        TestProcessStop.Run(() => false, () => calls.Add("close"), _ => { calls.Add("wait"); return true; }, _ => calls.Add("kill"));
        Check.Equal("close wait", string.Join(" ", calls), "a Test window that closes when asked is not killed");

        var pidFile = Path.Combine(AppContext.BaseDirectory, "stand-in-test-host-" + Guid.NewGuid().ToString("N") + ".pid");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--stand-in-test-host");
        info.ArgumentList.Add(pidFile);
        using var host = Process.Start(info)!;
        Process? child = null;
        try
        {
            for (var waited = 0; !File.Exists(pidFile) && waited < 20_000; waited += 50) Thread.Sleep(50);
            child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
            TestProcessStop.Run(host);
            Check.True(host.HasExited, "Stop Test must end the Test host");
            Check.False(child.HasExited, "but not its child: the WebView2 browser it started may be serving every other instance");
        }
        finally
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch { }
            try { if (child is { HasExited: false }) child.Kill(); } catch { }
            child?.Dispose();
            try { File.Delete(pidFile); } catch { }
        }
    }

    private static void DesktopSnapshotHelp()
    {
        // AUD-08 (help half). Killing mutation: restore the old help text, which did not say
        // that wallpaper mode and the screensaver preview never capture other windows.
        var field = ArgumentCatalog.Create().Groups.SelectMany(group => group.Fields).Single(candidate => candidate.Id == "desktopBackground");
        Check.Contains(field.Help ?? string.Empty, "Wallpaper mode and the screensaver preview use the desktop wallpaper only, never other windows.",
            "the help must say which hosts capture the live screen and which use the wallpaper image");
    }

    private static void SecondConfiguratorRefused()
    {
        // AUD-01. Killing mutation: ignore createdNew and always return a guard; the
        // same-thread second acquisition is then admitted.
        var unique = Guid.NewGuid().ToString("N");
        var storeA = Path.Combine(AppContext.BaseDirectory, "guard-" + unique, "a", "MatrixDesktopConfigurator.presets.json");
        var storeB = Path.Combine(AppContext.BaseDirectory, "guard-" + unique, "b", "MatrixDesktopConfigurator.presets.json");
        Check.True(SingleInstanceGuard.MutexName(storeA).StartsWith(@"Local\BigFnJ.MatrixDesktop.Configurator.", StringComparison.Ordinal)
            && SingleInstanceGuard.ObjectName(storeA).Length == "BigFnJ.MatrixDesktop.Configurator.".Length + 16,
            "the guard lives in the session-local namespace and names the store by 16 hex digits of its path hash");
        Check.Equal(SingleInstanceGuard.ObjectName(storeA), SingleInstanceGuard.ObjectName(storeA.ToLowerInvariant()),
            "Windows paths are case-insensitive, so one store must have one guard whatever the spelling");

        var first = SingleInstanceGuard.TryAcquire(storeA);
        try
        {
            Check.True(first is not null, "the first configurator on a store must start");
            using (var sameThread = SingleInstanceGuard.TryAcquire(storeA))
                Check.True(sameThread is null, "a second configurator on the same store must be refused, even on the owner's thread");
            SingleInstanceGuard? otherThread = null;
            var worker = new Thread(() => otherThread = SingleInstanceGuard.TryAcquire(storeA));
            worker.Start();
            worker.Join();
            using (otherThread)
                Check.True(otherThread is null, "a second configurator on the same store must be refused from another thread");
            Check.Equal(3, GuardProcessExitCode(storeA), "a second configurator PROCESS on the same store must be refused");
            using (var otherStore = SingleInstanceGuard.TryAcquire(storeB))
                Check.True(otherStore is not null, "a configurator with a different store must still run side by side");
        }
        finally
        {
            first?.Dispose();
        }

        using (var again = SingleInstanceGuard.TryAcquire(storeA))
            Check.True(again is not null, "closing the owner must release the store for the next configurator");
        Check.Equal(0, GuardProcessExitCode(storeA), "a later configurator process must start once the owner has gone");
    }

    // Program.cs: --try-configurator-guard <store> exits 0 when acquired, 3 when refused.
    private static int GuardProcessExitCode(string store)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--try-configurator-guard");
        info.ArgumentList.Add(store);
        using var process = Process.Start(info)!;
        Check.True(process.WaitForExit(20_000), "the guard probe process must finish");
        return process.ExitCode;
    }
}
