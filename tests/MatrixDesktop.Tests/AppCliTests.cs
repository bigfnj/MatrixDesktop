namespace MatrixDesktop.Tests;

internal static class AppCliTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("Defaults span all monitors with any-key and escape exit enabled", DefaultsAreDocumented),
        ("Windowed mode is selected by its flag", WindowedFlag),
        ("Single monitor mode targets the primary display", SingleMonitorFlag),
        ("A monitor index selects single monitor mode", MonitorIndexFlag),
        ("A negative monitor index is rejected and forwarded instead", NegativeMonitorIndexForwarded),
        ("A non numeric monitor index is rejected and forwarded instead", NonNumericMonitorForwarded),
        ("Borderless after a monitor index clears the index", BorderlessClearsMonitorIndex),
        ("Every documented windowing alias is recognised", WindowingAliases),
        ("Every documented exit control alias is recognised", ExitAliases),
        ("Every documented cursor and devtools alias is recognised", CursorAndDevToolsAliases),
        ("Web flags are forwarded rather than consumed", WebFlagsForwarded),
        ("A space separated web flag keeps its value together", WebFlagValuePreserved),
        ("An empty string value survives for the web layer", EmptyStringValueSurvives),
        ("A raw query string argument is forwarded untouched", RawQueryForwarded),
        ("Wrapper flags are stripped from the forwarded arguments", WrapperFlagsStripped),
        ("A boolean wrapper flag swallows an explicit value (MD-10)", BooleanFlagSwallowsValue),
    ];

    private static AppOptions Parse(out IReadOnlyList<string> passthrough, params string[] args)
        => AppCli.Parse(args, out passthrough);

    private static void DefaultsAreDocumented()
    {
        var o = Parse(out var rest);
        Check.Equal(WindowMode.BorderlessSpanAll, o.WindowMode, "the README documents borderless across all monitors as the default");
        Check.True(o.ExitOnAnyKey, "any-key exit is documented as on by default");
        Check.True(o.ExitOnEsc, "escape exit is documented as on by default");
        Check.False(o.TopMost, "topmost is documented as off by default");
        Check.False(o.HideCursor, "the cursor is documented as visible by default");
        Check.False(o.GlobalKeyExit, "key exit is documented as foreground-only by default");
        Check.False(o.DisableDevTools, "devtools are documented as available by default");
        Check.Equal(0, rest.Count, "no arguments means nothing to forward");
    }

    private static void WindowedFlag()
    {
        var o = Parse(out _, "--windowed");
        Check.Equal(WindowMode.Windowed, o.WindowMode, "--windowed must give a normal resizable window");
    }

    private static void SingleMonitorFlag()
    {
        var o = Parse(out _, "--single-monitor");
        Check.Equal(WindowMode.BorderlessSingleMonitor, o.WindowMode, "--single-monitor is documented as borderless on one display");
        Check.False(o.MonitorIndex.HasValue, "with no index given, the primary display is used");
    }

    private static void MonitorIndexFlag()
    {
        var o = Parse(out _, "--monitor", "1");
        Check.Equal(WindowMode.BorderlessSingleMonitor, o.WindowMode, "--monitor N implies single monitor mode");
        Check.Equal(1, o.MonitorIndex ?? -1, "the index is 0 based, so 1 is the second display");
    }

    private static void NegativeMonitorIndexForwarded()
    {
        var o = Parse(out var rest, "--monitor", "-1");
        Check.Equal(WindowMode.BorderlessSpanAll, o.WindowMode, "an invalid index must not change the window mode");
        Check.Equal(2, rest.Count, "an unparsed flag and its value are forwarded rather than silently dropped");
    }

    private static void NonNumericMonitorForwarded()
    {
        var o = Parse(out var rest, "--monitor", "left");
        Check.Equal(WindowMode.BorderlessSpanAll, o.WindowMode, "a non numeric index must not change the window mode");
        Check.Equal(2, rest.Count, "the flag and its value are preserved for the web layer to reject");
    }

    private static void BorderlessClearsMonitorIndex()
    {
        var o = Parse(out _, "--monitor", "1", "--borderless");
        Check.Equal(WindowMode.BorderlessSpanAll, o.WindowMode, "later flags win");
        Check.False(o.MonitorIndex.HasValue, "spanning all monitors is incompatible with a single index, so it is cleared");
    }

    private static void WindowingAliases()
    {
        foreach (var alias in new[] { "borderless", "fullscreen", "span", "span-all", "spanall" })
        {
            var o = Parse(out var rest, "--windowed", "--" + alias);
            Check.Equal(WindowMode.BorderlessSpanAll, o.WindowMode, $"--{alias} is a documented span-all alias");
            Check.Equal(0, rest.Count, $"--{alias} must be consumed, not forwarded to the web layer");
        }
        foreach (var alias in new[] { "single-monitor", "singlemonitor" })
        {
            var o = Parse(out _, "--" + alias);
            Check.Equal(WindowMode.BorderlessSingleMonitor, o.WindowMode, $"--{alias} is a documented single monitor alias");
        }
        foreach (var alias in new[] { "working-area", "workingarea" })
        {
            var o = Parse(out _, "--" + alias);
            Check.True(o.UseWorkingArea, $"--{alias} is a documented taskbar-safe alias");
        }
    }

    private static void ExitAliases()
    {
        foreach (var alias in new[] { "no-exit-on-any-key", "no-anykey-exit" })
        {
            Check.False(Parse(out _, "--" + alias).ExitOnAnyKey, $"--{alias} must disable any-key exit");
        }
        foreach (var alias in new[] { "exit-on-any-key", "exit-on-anykey", "anykey-exit" })
        {
            Check.True(Parse(out _, "--no-exit-on-any-key", "--" + alias).ExitOnAnyKey, $"--{alias} must re-enable any-key exit");
        }
        foreach (var alias in new[] { "no-esc-exit", "noesc-exit", "no-esc" })
        {
            Check.False(Parse(out _, "--" + alias).ExitOnEsc, $"--{alias} must disable escape exit");
        }
        foreach (var alias in new[] { "exit-on-esc", "esc-exit", "exitonesc", "escexit" })
        {
            Check.True(Parse(out _, "--no-esc", "--" + alias).ExitOnEsc, $"--{alias} must re-enable escape exit");
        }
        foreach (var alias in new[] { "global-key-exit", "globalkey-exit", "global-exit-on-key", "background-key-exit" })
        {
            Check.True(Parse(out _, "--" + alias).GlobalKeyExit, $"--{alias} must enable exit while unfocused");
        }
        foreach (var alias in new[] { "foreground-key-exit", "foregroundkey-exit", "require-foreground-key-exit",
                                      "requireforeground-key-exit", "no-global-key-exit", "noglobal-key-exit" })
        {
            Check.False(Parse(out _, "--global-key-exit", "--" + alias).GlobalKeyExit, $"--{alias} must restore foreground-only exit");
        }
    }

    private static void CursorAndDevToolsAliases()
    {
        foreach (var alias in new[] { "hide-cursor", "hidecursor" })
        {
            Check.True(Parse(out _, "--" + alias).HideCursor, $"--{alias} must hide the cursor");
        }
        foreach (var alias in new[] { "show-cursor", "showcursor" })
        {
            Check.False(Parse(out _, "--hide-cursor", "--" + alias).HideCursor, $"--{alias} must restore the cursor");
        }
        foreach (var alias in new[] { "no-devtools", "nodevtools" })
        {
            Check.True(Parse(out _, "--" + alias).DisableDevTools, $"--{alias} must disable devtools");
        }
        Check.False(Parse(out _, "--no-devtools", "--devtools").DisableDevTools, "--devtools must re-enable them");
        foreach (var alias in new[] { "topmost" })
        {
            Check.True(Parse(out _, "--" + alias).TopMost, $"--{alias} must pin the window on top");
        }
        foreach (var alias in new[] { "no-topmost", "notopmost" })
        {
            Check.False(Parse(out _, "--topmost", "--" + alias).TopMost, $"--{alias} must unpin the window");
        }
    }

    private static void WebFlagsForwarded()
    {
        Parse(out var rest, "--effect=mirror");
        Check.Equal(1, rest.Count, "an unrecognised flag belongs to the web layer");
        Check.Equal("--effect=mirror", rest[0], "the token is forwarded verbatim so MatrixArgs can parse it");
    }

    private static void WebFlagValuePreserved()
    {
        Parse(out var rest, "--effect", "mirror");
        Check.Equal(2, rest.Count, "a consumed value must be put back, or --effect would arrive with no value");
        Check.Equal("--effect", rest[0], "the key keeps its prefix");
        Check.Equal("mirror", rest[1], "the value follows immediately, preserving the pairing");
    }

    private static void EmptyStringValueSurvives()
    {
        // Smoke plan case 5 passes --palette "" and expects a safe fallback rather than a crash.
        Parse(out var rest, "--palette", string.Empty);
        Check.Equal("palette=", MatrixArgs.BuildQueryString(rest),
            "an empty value must reach the web layer so its parser can reject it and fall back");
    }

    private static void RawQueryForwarded()
    {
        Parse(out var rest, "?version=3d&effect=plain");
        Check.Equal(1, rest.Count, "a raw query string is not a wrapper flag");
        Check.Equal("?version=3d&effect=plain", rest[0], "it must arrive unchanged for the raw-query branch to recognise it");
    }

    private static void WrapperFlagsStripped()
    {
        Parse(out var rest, "--windowed", "--hide-cursor", "--effect", "stripes", "--topmost");
        Check.Equal(2, rest.Count, "three wrapper flags are consumed and only the web flag pair remains");
        Check.Equal("--effect", rest[0], "the surviving token is the web flag");
    }

    private static void BooleanFlagSwallowsValue()
    {
        // Current behaviour, recorded so the divergence from the configurator's importer is
        // visible. The guide documents app flags as bare switches with explicit negations,
        // and the parser consumes a following value token then discards it, so
        // '--topmost false' means topmost ON. ArgumentImporter honours the value instead,
        // which is why an imported command can misrepresent what it does.
        var o = Parse(out var rest, "--topmost", "false");
        Check.True(o.TopMost, "MD-10: the value is consumed and ignored, so this reads as topmost ON. Flip when MD-10 is fixed");
        Check.Equal(0, rest.Count, "MD-10: the swallowed value is not forwarded either, so it vanishes entirely");
    }
}
