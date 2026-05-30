using System;

namespace MatrixDesktop.Shared;

// Shared parsing/normalization primitives used by both the wrapper CLI parser
// (MatrixDesktop/AppCli.cs) and the configurator's command importer
// (MatrixDesktopConfigurator/ArgumentImporter.cs). Both previously implemented
// these helpers independently — keep them in one place so adding a new boolean
// alias or canonical key spelling is a one-line change instead of two.
internal static class FlagNormalization
{
    // Liberal boolean parser matching how the rest of the codebase treats flags.
    // Returns defaultWhenMissing for null/empty so callers can model "bare flag
    // present without a value" as true.
    public static bool ParseBool(string? value, bool defaultWhenMissing)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultWhenMissing;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "y" or "yes" or "on" => true,
            "0" or "false" or "n" or "no" or "off" => false,
            var v => v.Contains("true", StringComparison.OrdinalIgnoreCase),
        };
    }

    public static bool TryParseInt(string? value, out int result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return int.TryParse(value.Trim(), out result);
    }

    // Forgiving canonicalisation for CLI key names:
    //   "--exit_on_esc"  →  "exit-on-esc"
    //   "  Window  "     →  "window"
    // Leaves the result without the leading dashes; callers strip those before
    // calling. Done in one allocation via string.Create.
    public static string NormalizeKey(string? key)
    {
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
