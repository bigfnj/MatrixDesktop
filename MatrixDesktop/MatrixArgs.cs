using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text;

namespace MatrixDesktop;

internal static class MatrixArgs
{
    // These flags are treated as "true" if specified without a value.
    private static readonly FrozenSet<string> BoolKeys = new[]
    {
        "camera",
        "volumetric",
        "glyphFlip",
        "loops",
        "skipIntro",
        "suppressWarnings",
        "once",
        "isometric",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsHelpRequested(string[]? args)
    {
        if (args is null || args.Length == 0) return false;

        foreach (var arg in args)
        {
            var t = (arg ?? string.Empty).Trim();
            if (IsHelpToken(t)) return true;
        }

        return false;
    }

    /// <summary>
    /// Converts command-line args into a URL query string (without the leading '?').
    /// 
    /// Supported input forms:
    ///   - Raw query string: "?version=3d&effect=mirror"
    ///   - Key/value pairs: "version=3d" "effect=mirror"
    ///   - GNU-style: "--version=3d" "--effect=mirror"
    ///   - Windows-style: "/version=3d" "/effect=mirror"
    ///   - Space-separated: "--version" "3d"  (also works for negative numeric values)
    /// </summary>
    public static string BuildQueryString(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return string.Empty;
        }

        // If a single raw query string is provided, accept it as-is.
        if (args.Count == 1)
        {
            var single = (args[0] ?? string.Empty).Trim();
            if (single.StartsWith("?", StringComparison.Ordinal))
            {
                return single[1..];
            }

            // People sometimes paste "a=b&c=d" without the leading '?'.
            if (single.Contains('=') && single.Contains('&'))
            {
                return single.TrimStart('?');
            }
        }

        // Parse into pairs; last value wins for duplicates.
        var final = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Count; i++)
        {
            var token = (args[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (IsHelpToken(token))
            {
                continue;
            }

            var t = StripFlagPrefix(token);

            string key;
            string value;

            var eq = t.IndexOf('=');
            if (eq >= 0)
            {
                key = t[..eq].Trim();
                value = t[(eq + 1)..];
            }
            else
            {
                key = t.Trim();

                // Support: --key value
                if (i + 1 < args.Count)
                {
                    var next = (args[i + 1] ?? string.Empty).Trim();
                    if (!LooksLikeKeyToken(next))
                    {
                        value = next;
                        i++; // consume the value token
                    }
                    else if (BoolKeys.Contains(key))
                    {
                        value = "true";
                    }
                    else
                    {
                        // Unknown key without a value - treat as a typo or unsupported parameter
                        // Log a warning in debug builds, but silently drop in production
                        System.Diagnostics.Debug.WriteLine($"[MatrixArgs] Warning: Unknown parameter '{key}' with no value was ignored.");
                        continue;
                    }
                }
                else if (BoolKeys.Contains(key))
                {
                    value = "true";
                }
                else
                {
                    // Unknown key without a value - treat as a typo or unsupported parameter
                    // Log a warning in debug builds, but silently drop in production
                    System.Diagnostics.Debug.WriteLine($"[MatrixArgs] Warning: Unknown parameter '{key}' with no value was ignored.");
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }


            var finalValue = value ?? string.Empty;
            if (BoolKeys.Contains(key))
            {
                finalValue = NormalizeBoolValue(finalValue);
            }

            final[key] = finalValue;
        }

        if (final.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var kv in final)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value));
        }

        return sb.ToString();
    }


    private static string NormalizeBoolValue(string value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length == 0) return "true";

        // The upstream parser treats values containing "true" as true.
        // Map common CLI boolean forms into explicit true/false strings.
        switch (v.ToLowerInvariant())
        {
            case "1":
            case "y":
            case "yes":
            case "on":
                return "true";
            case "0":
            case "n":
            case "no":
            case "off":
                return "false";
            default:
                return v;
        }
    }

    private static bool LooksLikeKeyToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        var t = token.Trim();
        if (IsHelpToken(t)) return true;

        // Treat explicit flag prefixes as a new key.
        if (t.StartsWith("--", StringComparison.Ordinal) || t.StartsWith("/", StringComparison.Ordinal))
        {
            return true;
        }

        // Treat key=value as a new key.
        if (t.Contains('='))
        {
            return true;
        }

        return false;
    }

    private static string StripFlagPrefix(string token)
    {
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            return token[2..];
        }

        if (token.StartsWith("/", StringComparison.Ordinal))
        {
            return token[1..];
        }

        return token;
    }

    internal static bool IsHelpToken(string token)
    {
        return token.Equals("--help", StringComparison.OrdinalIgnoreCase)
               || token.Equals("-h", StringComparison.OrdinalIgnoreCase)
               || token.Equals("/?", StringComparison.OrdinalIgnoreCase)
               || token.Equals("help", StringComparison.OrdinalIgnoreCase);
    }
}
