using MatrixDesktop;

internal static class ScreensaverFocusTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Native screensaver overrides saved window flags after the profile", WindowOverrides),
        ("Native screensaver requests foreground after renderer-ready callbacks", ReadyBeforeForeground),
        ("Native screensaver retains its bounded startup focus retries", RetryWindow),
    ];

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void WindowOverrides()
    {
        string[] profile = ["--wallpaper", "--working-area=true", "--topmost=false"];
        var saver = new HostLaunchPlan(profile, HostLaunchKind.Screensaver);
        var effective = ScreensaverWindowPolicy.EffectiveArguments(saver);
        Require(effective.Take(profile.Length).SequenceEqual(profile), "Saved profile arguments must precede the forced screensaver overrides.");
        foreach (var forced in new[] { "--borderless", "--working-area=false", "--hide-cursor", "--exit-on-any-key", "--exit-on-esc", "--global-key-exit", "--topmost" })
            Require(Array.LastIndexOf(effective, forced) >= profile.Length, $"Screensaver mode must force {forced} after saved values.");
        foreach (var kind in new[] { HostLaunchKind.Standard, HostLaunchKind.ScreensaverPreview })
            Require(ReferenceEquals(ScreensaverWindowPolicy.EffectiveArguments(new HostLaunchPlan(profile, kind)), profile), "Ordinary launches and previews must preserve their window arguments.");
    }

    private static void ReadyBeforeForeground()
    {
        foreach (var kind in Enum.GetValues<HostLaunchKind>())
        {
            var events = new List<string>();
            var foreground = "startup";
            ScreensaverWindowPolicy.RendererReady(new HostLaunchPlan([], kind),
                () => { foreground = "startup-service-window"; events.Add("ready"); },
                () => { foreground = "screensaver"; events.Add("foreground"); });
            if (kind == HostLaunchKind.Screensaver)
                Require(events.SequenceEqual(new[] { "ready", "foreground" }) && foreground == "screensaver", "The genuine screensaver must request foreground after readiness callbacks can change focus.");
            else Require(events.SequenceEqual(new[] { "ready" }) && foreground == "startup-service-window", "Wallpaper, ordinary launches and previews must not request foreground after renderer readiness.");
        }
    }

    private static void RetryWindow()
    {
        foreach (var kind in Enum.GetValues<HostLaunchKind>())
        {
            var plan = new HostLaunchPlan([], kind);
            Require(!ScreensaverWindowPolicy.StopAfterOwnership(plan, false), "A missing foreground window must retain the bounded retry.");
            Require(ScreensaverWindowPolicy.StopAfterOwnership(plan, true) == (kind != HostLaunchKind.Screensaver), "Only the genuine screensaver must retain retries after early ownership during startup.");
        }
    }
}
