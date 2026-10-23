using System;
using System.Text;

namespace Shared;

// JSON stays structured in saved presets and direct argv. Copied commands use a
// shell-neutral alphabet so quotes, percent signs and ampersands stay literal.
internal static class StructuredArgumentCodec
{
    internal const string Prefix = "json64:";
    internal const int MaximumBytes = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = StrictUtf8.GetBytes(text);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("The structured argument exceeds 64 KB.", nameof(text));
        return Prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string text, out string decoded)
    {
        decoded = string.Empty;
        if (text is null) return false;
        try
        {
            if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            {
                if (StrictUtf8.GetByteCount(text) > MaximumBytes) return false;
                decoded = text;
                return true;
            }
            var value = text[Prefix.Length..];
            if (value.Length > ((MaximumBytes + 2) / 3) * 4 || value.Length % 4 == 1) return false;
            foreach (var character in value)
                if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') return false;
            var padding = (4 - value.Length % 4) % 4;
            var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', padding));
            if (bytes.Length > MaximumBytes) return false;
            decoded = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException or EncoderFallbackException) { return false; }
    }
}
