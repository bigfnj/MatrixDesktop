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
        "clickRipples",
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

        // A single raw query string is a documented paste form.
        //
        // MD-17: this used to return the string verbatim, so a space arrived unescaped and
        // produced an invalid URL, and a '#' silently truncated every parameter after it by
        // starting a fragment. Both branches now percent ENCODE through Encode().
        //
        // They deliberately differ on DECODING, and that asymmetry is correct rather than an
        // oversight, so do not "align" them:
        //
        //   --url=a%20b   a command-line value is literal text. It is encoded but not
        //                 decoded, so the web layer decodes back to exactly "a%20b", which
        //                 is what the user typed. A literal space is written --url "a b".
        //   ?url=a%20b    a pasted query string is already encoded BY DEFINITION, so %20
        //                 means a space. It is decoded first, then re-encoded, and the web
        //                 layer sees "a b".
        //
        // Decoding the command-line form would silently reinterpret any filename containing
        // a percent sign. Both behaviours are pinned by tests in MatrixArgsTests.
        if (args.Count == 1)
        {
            var single = (args[0] ?? string.Empty).Trim();
            var isRawQuery = single.StartsWith("?", StringComparison.Ordinal)
                             || (single.Contains('=') && single.Contains('&'));

            if (isRawQuery)
            {
                return NormalizeRawQuery(single.TrimStart('?'));
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

        return Encode(final);
    }


    // Splits a raw query on '&', decodes each side of the first '=', then re-encodes. The
    // decode step matters: a pasted query usually arrives already percent-encoded, and
    // encoding it a second time without decoding would turn "%20" into "%2520".
    private static string NormalizeRawQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var final = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Trim();
            if (pair.Length == 0) continue;

            var eq = pair.IndexOf('=');
            string key;
            string value;

            if (eq < 0)
            {
                key = Unescape(pair);
                value = string.Empty;
            }
            else
            {
                key = Unescape(pair[..eq]);
                value = Unescape(pair[(eq + 1)..]);
            }

            if (string.IsNullOrWhiteSpace(key)) continue;

            if (BoolKeys.Contains(key))
            {
                value = NormalizeBoolValue(value);
            }

            final[key] = value;
        }

        return Encode(final);
    }

    private static string Unescape(string value)
    {
        try
        {
            // '+' means space in form encoding, which is how these strings are usually
            // produced when copied out of a browser address bar.
            return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
        }
        catch (UriFormatException)
        {
            // A stray '%' that is not a valid escape. Take the text literally rather than
            // dropping the parameter.
            return value;
        }
    }

    private static string Encode(Dictionary<string, string> pairs)
    {
        var sb = new StringBuilder();
        foreach (var kv in pairs)
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

        // Maps common CLI boolean spellings onto the exact strings the web layer accepts.
        //
        // The comment that used to sit here claimed "the upstream parser treats values
        // containing 'true' as true". That is wrong, and it matters because it describes the
        // wrong side of the boundary. web/js/config.js:402 is
        //     const isTrue = (s) => s.toLowerCase() === "true";
        // which is STRICT equality. The substring behaviour belongs to
        // Shared/FlagNormalization.ParseBool, which serves wrapper flags only.
        //
        // So the two boolean parsers in this codebase genuinely differ: a wrapper flag
        // treats any value containing "true" as true, while a web flag requires exactly
        // "true". That is why this method normalises the recognised spellings here rather
        // than passing them through and hoping. Anything unrecognised is forwarded verbatim
        // and the web layer will read it as false.
        // NormalizeRawQuery calls this too, so the raw-query path gets the same treatment.
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
