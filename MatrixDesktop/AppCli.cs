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

            var k = NormalizeKey(key);
            switch (k)
            {
                case "windowed":
                    mode = WindowMode.Windowed;
                    break;

                case "borderless":
                case "fullscreen":
                case "span":
                case "span-all":
                case "spanall":
                    mode = WindowMode.BorderlessSpanAll;
                    monitorIndex = null;
                    break;

                case "single-monitor":
                case "singlemonitor":
                    mode = WindowMode.BorderlessSingleMonitor;
                    monitorIndex = null; // primary
                    break;

                case "monitor":
                    if (TryParseInt(value, out var idx) && idx >= 0)
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
                    useWorkingArea = true;
                    break;

                case "topmost":
                    topMost = true;
                    break;

                case "no-topmost":
                case "notopmost":
                    topMost = false;
                    break;

                case "exit-on-esc":
                case "esc-exit":
                case "exitonesc":
                case "escexit":
                    exitOnEsc = true;
                    break;

                case "no-esc-exit":
                case "noesc-exit":
                case "no-esc":
                    exitOnEsc = false;
                    break;

                case "exit-on-any-key":
                case "exit-on-anykey":
                case "anykey-exit":
                    exitOnAnyKey = true;
                    break;

                case "no-exit-on-any-key":
                case "no-anykey-exit":
                    exitOnAnyKey = false;
                    break;

                case "global-key-exit":
                case "globalkey-exit":
                case "global-exit-on-key":
                case "background-key-exit":
                    globalKeyExit = true;
                    break;

                case "foreground-key-exit":
                case "foregroundkey-exit":
                case "require-foreground-key-exit":
                case "requireforeground-key-exit":
                case "no-global-key-exit":
                case "noglobal-key-exit":
                    globalKeyExit = false;
                    break;

                case "hide-cursor":
                case "hidecursor":
                    hideCursor = true;
                    break;

                case "show-cursor":
                case "showcursor":
                    hideCursor = false;
                    break;

                case "no-devtools":
                case "nodevtools":
                    disableDevTools = true;
                    break;

                case "devtools":
                    disableDevTools = false;
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

    private static bool TryParseInt(string? value, out int result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return int.TryParse(value.Trim(), out result);
    }

    private static string NormalizeKey(string key)
    {
        // Be forgiving with common CLI formatting variations.
        // e.g. "--exit_on_esc" should behave like "--exit-on-esc".
        // Single-pass: trim, lower-case, and replace '_' with '-' in one allocation.
        var k = key ?? string.Empty;
        var start = 0;
        var end = k.Length;
        while (start < end && char.IsWhiteSpace(k[start])) start++;
        while (end > start && char.IsWhiteSpace(k[end - 1])) end--;
        if (start >= end) return string.Empty;

        return string.Create(end - start, (k, start), static (span, state) =>
        {
            var (src, offset) = state;
            for (var i = 0; i < span.Length; i++)
            {
                var c = src[offset + i];
                span[i] = c == '_' ? '-' : char.ToLowerInvariant(c);
            }
        });
    }
}
