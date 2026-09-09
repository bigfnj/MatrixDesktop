using System;
using System.Collections.Generic;

namespace MatrixDesktop;

internal enum WindowMode
{
    /// <summary>
    /// Borderless window spanning the full virtual desktop (all monitors).
    /// </summary>
    BorderlessSpanAll,

    /// <summary>
    /// Borderless window constrained to a single monitor.
    /// </summary>
    BorderlessSingleMonitor,

    /// <summary>
    /// Normal resizable window.
    /// </summary>
    Windowed,
}

internal sealed class AppOptions
{
    public WindowMode WindowMode { get; init; } = WindowMode.BorderlessSpanAll;
    public int? MonitorIndex { get; init; } = null;
    public bool UseWorkingArea { get; init; } = false;
    public bool TopMost { get; init; } = false;
    public bool ExitOnEsc { get; init; } = true;
    public bool ExitOnAnyKey { get; init; } = true;
    public bool GlobalKeyExit { get; init; } = false;
    public bool HideCursor { get; init; } = false;
    public bool DisableDevTools { get; init; } = false;

    public static AppOptions Default { get; } = new();
}

internal static class AppCli
{
    public static bool IsHelpRequested(string[]? args)
    {
        return MatrixArgs.IsHelpRequested(args);
    }

    /// <summary>
    /// Parses wrapper-level CLI flags and returns:
    /// - options controlling windowing / wrapper behavior
    /// - passthrough args to be forwarded to MatrixArgs.BuildQueryString()
    /// </summary>
    public static AppOptions Parse(string[]? args, out IReadOnlyList<string> passthroughArgs)
    {
        if (args is null || args.Length == 0)
        {
            passthroughArgs = [];
            return AppOptions.Default;
        }

        var mode = WindowMode.BorderlessSpanAll;
        int? monitorIndex = null;
        var useWorkingArea = false;
        var topMost = false;
        var exitOnEsc = true;
        var exitOnAnyKey = true;
        var globalKeyExit = false;
        var hideCursor = false;
        var disableDevTools = false;

        var passthrough = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            var raw = (args[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // Let the central help token detector handle this.
            if (MatrixArgs.IsHelpToken(raw))
            {
                continue;
            }

            if (!LooksLikeFlag(raw))
            {
                passthrough.Add(raw);
                continue;
            }

            var token = StripFlagPrefix(raw);
            var (key, value, consumedValue) = SplitKeyValue(token, args, ref i);

            if (string.IsNullOrWhiteSpace(key))
            {
                // Preserve anything we can't interpret.
                passthrough.Add(raw);
                if (consumedValue)
                {
                    // If we consumed a value token while failing to parse the key, put it back too.
                    // This should be extremely rare.
                    passthrough.Add(value ?? string.Empty);
                }
                continue;
            }

            var k = Shared.FlagNormalization.NormalizeKey(key);
            switch (k)
            {
                case "windowed":
                    mode = WindowMode.Windowed;
                    WarnIfValueIgnored(k, value, consumedValue);
                    break;

                case "borderless":
                case "fullscreen":
                case "span":
                case "span-all":
                case "spanall":
                    mode = WindowMode.BorderlessSpanAll;
                    monitorIndex = null;
                    WarnIfValueIgnored(k, value, consumedValue);
                    break;

                case "single-monitor":
                case "singlemonitor":
                    mode = WindowMode.BorderlessSingleMonitor;
                    monitorIndex = null; // primary
                    // "--single-monitor 1" used to consume the 1, drop it silently, and run
                    // on the PRIMARY display. Very easy to type when "--monitor 1" is the
                    // flag two lines above it in the help text.
                    WarnIfValueIgnored(k, value, consumedValue);
                    break;

                case "monitor":
                    if (Shared.FlagNormalization.TryParseInt(value, out var idx) && idx >= 0)
                    {
                        mode = WindowMode.BorderlessSingleMonitor;
                        monitorIndex = idx;
                    }
                    else
                    {
                        // If invalid, keep args for the web side rather than silently swallowing.
                        passthrough.Add(raw);
                        if (consumedValue && value is not null) passthrough.Add(value);
                    }
                    break;

                case "working-area":
                case "workingarea":
                    useWorkingArea = OnValue(value);
                    break;

                case "topmost":
                    topMost = OnValue(value);
                    break;

                case "no-topmost":
                case "notopmost":
                    topMost = false;
                    WarnIfValueIgnored(k, value, consumedValue);
                    break;

                case "exit-on-esc":
                case "esc-exit":
                case "exitonesc":
                case "escexit":
                    exitOnEsc = OnValue(value);
                    break;

                case "no-esc-exit":
                case "noesc-exit":
                case "no-esc":
                    exitOnEsc = false;
                    WarnIfValueIgnored(k, value, consumedValue);
                    break;

                case "exit-on-any-key":
                case "exit-on-anykey":
                case "anykey-exit":
                    exitOnAnyKey = OnValue(value);
                    break;

                case "no-exit-on-any-key":
                case "no-anykey-exit":
                    exitOnAnyKey = false;
                    WarnIfValueIgnored(k, value, consumedValue);
                    break;

                case "global-key-exit":
                case "globalkey-exit":
                case "global-exit-on-key":
                case "background-key-exit":
                    globalKeyExit = OnValue(value);
                    break;

                case "foreground-key-exit":
                case "foregroundkey-exit":
                case "require-foreground-key-exit":
                case "requireforeground-key-exit":
                case "no-global-key-exit":
                case "noglobal-key-exit":
                    globalKeyExit = false;
                    WarnIfValueIgnored(k, value, consumedValue);
                    break;

                case "hide-cursor":
                case "hidecursor":
                    hideCursor = OnValue(value);
                    break;

                case "show-cursor":
                case "showcursor":
                    hideCursor = false;
                    WarnIfValueIgnored(k, value, consumedValue);
                    break;

                case "no-devtools":
                case "nodevtools":
                    disableDevTools = OnValue(value);
                    break;

                case "devtools":
                    disableDevTools = false;
                    WarnIfValueIgnored(k, value, consumedValue);
                    break;

                default:
                    // Not a wrapper-level flag; forward it to the web query-string builder.
                    passthrough.Add(raw);
                    if (consumedValue && value is not null)
                    {
                        // Important: preserve space-separated forms like "--effect mirror".
                        passthrough.Add(value);
                    }
                    break;
            }
        }

        passthroughArgs = passthrough;
        return new AppOptions
        {
            WindowMode = mode,
            MonitorIndex = monitorIndex,
            UseWorkingArea = useWorkingArea,
            TopMost = topMost,
            ExitOnEsc = exitOnEsc,
            ExitOnAnyKey = exitOnAnyKey,
            GlobalKeyExit = globalKeyExit,
            HideCursor = hideCursor,
            DisableDevTools = disableDevTools,
        };
    }

    public static string GetHelpText()
    {
        // Keep this message short enough for a MessageBox.
        return string.Join(Environment.NewLine, new[]
        {
            "MatrixDesktop.exe [app flags] [web flags]",
            "",
            "App flags (handled by the desktop wrapper):",
            "  --windowed             Run in a normal resizable window.",
            "  --borderless           Force borderless fullscreen spanning all monitors (default).",
            "  --single-monitor        Borderless fullscreen on the primary monitor.",
            "  --monitor N             Borderless fullscreen on monitor index N (0-based).",
            "  --working-area          Use WorkingArea (won't cover taskbars).",
            "  --topmost               Keep the window always on top.",
            "  --hide-cursor           Hide the mouse cursor while running.",
            "  --exit-on-esc           Exit on physical ESC key (enabled by default).",
            "  --no-esc-exit           Disable ESC-to-exit.",
            "  --exit-on-any-key       Exit on any physical key press (default).",
            "  --no-exit-on-any-key    Disable any-key exit.",
            "  --foreground-key-exit   Only exit on keypress when MatrixDesktop is foreground (default).",
            "  --global-key-exit       Exit on keypress even when not focused (use with caution).",
            "  --no-devtools           Disable WebView2 DevTools.",
            "",
            "Web flags (forwarded to the embedded web app as URL query params):",
            "  Examples: --version 3d --effect mirror --camera true",
            "",
            "Examples:",
            "  MatrixDesktop.exe --windowed --version 3d",
            "  MatrixDesktop.exe --monitor 1 --effect mirror",
            "  MatrixDesktop.exe \"?version=3d&effect=mirror\"",
        });
    }

    // MD-10. These flags used to consume a following value token and then discard it, so
    // "--topmost false" enabled topmost and the "false" vanished with no diagnostic. The
    // configurator's importer honoured the same value, which meant importing a hand-written
    // command produced a draft that disagreed with what the command actually did.
    //
    // The convention now matches ArgumentImporter.ApplyAppPair exactly, and the
    // alias-parity test in the regression harness keeps the two from drifting again:
    //   - a primary form takes an optional value and defaults to true, so "--topmost",
    //     "--topmost true" and "--topmost yes" all enable it and "--topmost false" does not
    //   - an explicitly negative form such as "--no-topmost" always means false, because
    //     "--no-topmost false" is a double negative nobody should have to reason about
    private static bool OnValue(string? value)
        => Shared.FlagNormalization.ParseBool(value, defaultWhenMissing: true);

    private static void WarnIfValueIgnored(string key, string? value, bool consumedValue)
    {
        // Gated on the value alone, NOT on consumedValue. SplitKeyValue reports
        // consumedValue: false for the "--flag=value" spelling because the value came from
        // the same token, so gating on it meant "--no-topmost false" warned while
        // "--no-topmost=false" was silently ignored, and "=" is the form the README and the
        // argument guide document.
        _ = consumedValue;
        if (value is null)
        {
            return;
        }

        // Deliberately a log line rather than silence. The token is gone either way, but
        // "--no-topmost false" is a mistake worth telling someone about.
        Shared.Logger.Warn(
            $"'--{key}' always means false, so the value '{value}' after it was ignored. " +
            $"Use the positive form if you meant to pass a value.");
    }

    private static bool LooksLikeFlag(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var t = token.Trim();
        return t.StartsWith("--", StringComparison.Ordinal) || t.StartsWith("/", StringComparison.Ordinal);
    }

    private static string StripFlagPrefix(string token)
    {
        if (token.StartsWith("--", StringComparison.Ordinal)) return token[2..];
        if (token.StartsWith("/", StringComparison.Ordinal)) return token[1..];
        return token;
    }

    private static (string key, string? value, bool consumedValue) SplitKeyValue(string token, string[] args, ref int i)
    {
        if (string.IsNullOrWhiteSpace(token)) return (string.Empty, null, false);

        var eq = token.IndexOf('=');
        if (eq >= 0)
        {
            var k = token[..eq].Trim();
            var v = token[(eq + 1)..];
            return (k, v, false);
        }

        // Support "--key value" forms.
        var key = token.Trim();
        if (i + 1 < args.Length)
        {
            var nextRaw = (args[i + 1] ?? string.Empty).Trim();
            if (!LooksLikeKeyToken(nextRaw))
            {
                i++; // consume
                return (key, nextRaw, true);
            }
        }

        return (key, null, false);
    }

    private static bool LooksLikeKeyToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var t = token.Trim();

        if (MatrixArgs.IsHelpToken(t)) return true;

        if (t.StartsWith("--", StringComparison.Ordinal) || t.StartsWith("/", StringComparison.Ordinal)) return true;
        if (t.Contains('=')) return true;
        return false;
    }

    // TryParseInt + NormalizeKey moved to MatrixDesktop.Shared.FlagNormalization
    // so the configurator's ArgumentImporter shares the same canonicalisation rules.
}
