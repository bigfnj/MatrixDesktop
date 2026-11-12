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

    // A text value (an image URL, a folder or a file) uses the same alphabet in a copied
    // command when it holds a character cmd or PowerShell expands even inside double quotes:
    // %, ! (delayed expansion), $ or a backtick, or a typographic double quote (U+201C to
    // U+201E), which ends a PowerShell double-quoted string (R3-07). The payload is the
    // value's JSON string literal, so a decoder can tell it from a structured object or
    // array, and no real URL or Windows path starts with json64:. Anything else decodes
    // to itself.
    private static readonly System.Text.Json.JsonSerializerOptions TextJsonOptions = new()
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool TextNeedsEncoding(string text) => text.AsSpan().IndexOfAny("%!$`\u201C\u201D\u201E") >= 0;

    public static string EncodeText(string text) => Encode(System.Text.Json.JsonSerializer.Serialize(text, TextJsonOptions));

    public static string DecodeText(string text)
    {
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal) || !TryDecode(text, out var json)) return text!;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.String ? document.RootElement.GetString()! : text;
        }
        catch (System.Text.Json.JsonException) { return text; }
    }
}
