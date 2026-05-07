using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace MatrixDesktopConfigurator;

internal sealed class ArgumentImporter
{
    private static readonly HashSet<string> StripeEffects = new(StringComparer.OrdinalIgnoreCase)
    {
        "stripes",
        "customStripes",
        "pride",
        "trans",
        "transPride",
    };

    private readonly ConfiguratorMetadata _metadata;
    private readonly Dictionary<string, ArgumentDefinition> _fieldsByKey;

    public ArgumentImporter(ConfiguratorMetadata metadata)
    {
        _metadata = metadata;
        _fieldsByKey = _metadata.Groups
            .SelectMany(static group => group.Fields)
            .SelectMany(static field => new[]
            {
                new KeyValuePair<string, ArgumentDefinition>(CanonicalKey(field.Id), field),
                new KeyValuePair<string, ArgumentDefinition>(CanonicalKey(field.ArgName), field),
            })
            .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Value, StringComparer.OrdinalIgnoreCase);
    }

    public ImportResult Import(string commandLine, JsonObject defaultDraft)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ArgumentException("Paste a MatrixDesktop command or argument line first.", nameof(commandLine));
        }

        var draft = StorageService.CloneObject(defaultDraft);
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignored = new List<string>();
        var tokens = TrimLauncherTokens(Tokenize(commandLine));

        for (var i = 0; i < tokens.Count; i++)
        {
            var raw = (tokens[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (IsRawQueryToken(raw))
            {
                foreach (var (queryKey, queryValue) in ParseQueryPairs(raw))
                {
                    if (!ApplyPair(draft, queryKey, queryValue, applied))
                    {
                        ignored.Add(queryKey);
                    }
                }

                continue;
            }

            var token = StripFlagPrefix(raw);
            var (key, value) = SplitKeyValue(token, tokens, ref i);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!ApplyPair(draft, key, value, applied))
            {
                ignored.Add(key);
            }
        }

        return new ImportResult
        {
            Draft = draft,
            Applied = applied.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Ignored = ignored.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    private bool ApplyPair(JsonObject draft, string key, string? value, HashSet<string> applied)
    {
        var canonical = CanonicalKey(key);
        if (ApplyAppPair(draft, canonical, value, applied))
        {
            return true;
        }

        var (fieldKey, isHsl) = ResolveWebKey(canonical);
        if (!_fieldsByKey.TryGetValue(fieldKey, out var field) || field.Scope != "web")
        {
            return false;
        }

        var node = ParseFieldValue(field, value, isHsl);
        if (node is null)
        {
            return false;
        }

        draft[field.Id] = node;
        applied.Add(field.Id);
        return true;
    }

    private static bool ApplyAppPair(JsonObject draft, string key, string? value, HashSet<string> applied)
    {
        switch (key)
        {
            case "windowed":
                draft["windowMode"] = "windowed";
                applied.Add("windowMode");
                return true;

            case "borderless":
            case "fullscreen":
            case "span":
            case "spanall":
                draft["windowMode"] = "borderless";
                draft["monitor"] = null;
                applied.Add("windowMode");
                return true;

            case "singlemonitor":
                draft["windowMode"] = "single-monitor";
                draft["monitor"] = null;
                applied.Add("windowMode");
                return true;

            case "monitor":
                if (!TryParseInt(value, out var monitor) || monitor < 0)
                {
                    return false;
                }

                draft["windowMode"] = "single-monitor";
                draft["monitor"] = monitor;
                applied.Add("windowMode");
                applied.Add("monitor");
                return true;

            case "workingarea":
                draft["workingArea"] = ParseBool(value, true);
                applied.Add("workingArea");
                return true;

            case "topmost":
                draft["topmost"] = ParseBool(value, true);
                applied.Add("topmost");
                return true;

            case "notopmost":
                draft["topmost"] = false;
                applied.Add("topmost");
                return true;

            case "hidecursor":
                draft["hideCursor"] = ParseBool(value, true);
                applied.Add("hideCursor");
                return true;

            case "showcursor":
                draft["hideCursor"] = false;
                applied.Add("hideCursor");
                return true;

            case "exitonesc":
            case "escexit":
                draft["exitOnEsc"] = ParseBool(value, true);
                applied.Add("exitOnEsc");
                return true;

            case "noescexit":
            case "noesc":
                draft["exitOnEsc"] = false;
                applied.Add("exitOnEsc");
                return true;

            case "exitonanykey":
            case "anykeyexit":
                draft["exitOnAnyKey"] = ParseBool(value, true);
                applied.Add("exitOnAnyKey");
                return true;

            case "noexitonanykey":
            case "noanykeyexit":
                draft["exitOnAnyKey"] = false;
                applied.Add("exitOnAnyKey");
                return true;

            case "globalkeyexit":
            case "globalexitonkey":
            case "backgroundkeyexit":
                draft["globalKeyExit"] = ParseBool(value, true);
                applied.Add("globalKeyExit");
                return true;

            case "foregroundkeyexit":
            case "requireforegroundkeyexit":
            case "noglobalkeyexit":
                draft["globalKeyExit"] = false;
                applied.Add("globalKeyExit");
                return true;

            case "nodevtools":
                draft["disableDevTools"] = ParseBool(value, true);
                applied.Add("disableDevTools");
                return true;

            case "devtools":
                draft["disableDevTools"] = false;
                applied.Add("disableDevTools");
                return true;
        }

        return false;
    }

    private static JsonNode? ParseFieldValue(ArgumentDefinition field, string? value, bool isHsl)
    {
        switch (field.Kind)
        {
            case "bool":
                return JsonValue.Create(ParseBool(value, true));

            case "number":
                return TryParseDouble(value, out var number)
                    ? JsonValue.Create(Clamp(number, field.Min, field.Max))
                    : null;

            case "select":
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                var option = field.Options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));
                return JsonValue.Create(option?.Value ?? value.Trim());

            case "text":
                return JsonValue.Create(value ?? string.Empty);

            case "color":
                return ParseColor(value, isHsl);

            case "palette":
                return ParsePalette(value, isHsl);

            case "stripes":
                return ParseStripes(value, isHsl);

            default:
                return string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value.Trim());
        }
    }

    private static JsonObject? ParseColor(string? value, bool isHsl)
    {
        var values = ParseFiniteList(value);
        if (values is null || values.Length != 3)
        {
            return null;
        }

        var rgb = isHsl ? HslToRgb(values[0], values[1], values[2]) : values;
        return ColorNode(rgb[0], rgb[1], rgb[2]);
    }

    private static JsonArray? ParsePalette(string? value, bool isHsl)
    {
        var values = ParseFiniteList(value);
        if (values is null || values.Length < 4 || values.Length % 4 != 0)
        {
            return null;
        }

        var result = new JsonArray();
        for (var i = 0; i < values.Length; i += 4)
        {
            var rgb = isHsl ? HslToRgb(values[i], values[i + 1], values[i + 2]) : values[i..(i + 3)];
            result.Add(new JsonObject
            {
                ["r"] = Clamp01(rgb[0]),
                ["g"] = Clamp01(rgb[1]),
                ["b"] = Clamp01(rgb[2]),
                ["at"] = Clamp01(values[i + 3]),
            });
        }

        return result;
    }

    private static JsonArray? ParseStripes(string? value, bool isHsl)
    {
        var values = ParseFiniteList(value);
        if (values is null || values.Length < 3 || values.Length % 3 != 0)
        {
            return null;
        }

        var result = new JsonArray();
        for (var i = 0; i < values.Length; i += 3)
        {
            var rgb = isHsl ? HslToRgb(values[i], values[i + 1], values[i + 2]) : values[i..(i + 3)];
            result.Add(ColorNode(rgb[0], rgb[1], rgb[2]));
        }

        return result;
    }

    private static JsonObject ColorNode(double r, double g, double b)
    {
        return new JsonObject
        {
            ["r"] = Clamp01(r),
            ["g"] = Clamp01(g),
            ["b"] = Clamp01(b),
        };
    }

    private static (string FieldKey, bool IsHsl) ResolveWebKey(string canonical)
    {
        return canonical switch
        {
            "backgroundrgb" => (CanonicalKey("backgroundColor"), false),
            "cursorrgb" => (CanonicalKey("cursorColor"), false),
            "glintrgb" => (CanonicalKey("glintColor"), false),
            "palettergb" => (CanonicalKey("palette"), false),
            "stripergb" => (CanonicalKey("stripeColors"), false),
            "colors" => (CanonicalKey("stripeColors"), false),
            "backgroundhsl" => (CanonicalKey("backgroundColor"), true),
            "cursorhsl" => (CanonicalKey("cursorColor"), true),
            "glinthsl" => (CanonicalKey("glintColor"), true),
            "palettehsl" => (CanonicalKey("palette"), true),
            "stripehsl" => (CanonicalKey("stripeColors"), true),
            "width" => (CanonicalKey("numColumns"), false),
            "droplength" => (CanonicalKey("raindropLength"), false),
            "angle" => (CanonicalKey("slant"), false),
            _ => (canonical, false),
        };
    }

    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var tokenStarted = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (tokenStarted)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            current.Append(c);
            tokenStarted = true;
        }

        if (tokenStarted)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static List<string> TrimLauncherTokens(List<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (IsMatrixDesktopExecutable(tokens[i]))
            {
                return tokens.Skip(i + 1).ToList();
            }
        }

        if (tokens.Count > 0 && string.Equals(tokens[0], "start", StringComparison.OrdinalIgnoreCase))
        {
            var i = 1;
            if (i < tokens.Count && tokens[i].Length == 0)
            {
                i++;
            }

            while (i < tokens.Count && IsStartOption(tokens[i]))
            {
                i++;
            }

            if (i < tokens.Count && IsLauncherCommand(tokens[i]))
            {
                i++;
            }

            return tokens.Skip(i).ToList();
        }

        if (tokens.Count > 0 && LooksLikeExecutable(tokens[0]))
        {
            return tokens.Skip(1).ToList();
        }

        return tokens;
    }

    private static bool IsRawQueryToken(string token)
    {
        if (token.StartsWith("?", StringComparison.Ordinal))
        {
            return true;
        }

        if (!token.Contains('&') || token.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var firstEquals = token.IndexOf('=');
        if (firstEquals <= 0)
        {
            return false;
        }

        var firstKey = CanonicalKey(token[..firstEquals].TrimStart('?'));
        return firstKey != "url";
    }

    private static IEnumerable<(string Key, string Value)> ParseQueryPairs(string raw)
    {
        var query = raw.Trim().TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            yield return (Unescape(part[..eq]), Unescape(part[(eq + 1)..]));
        }
    }

    private static (string Key, string? Value) SplitKeyValue(string token, List<string> tokens, ref int index)
    {
        var eq = token.IndexOf('=');
        if (eq >= 0)
        {
            return (token[..eq].Trim(), token[(eq + 1)..]);
        }

        var key = token.Trim();
        if (index + 1 >= tokens.Count)
        {
            return (key, null);
        }

        var next = (tokens[index + 1] ?? string.Empty).Trim();
        if (LooksLikeKeyToken(next) && !(CanonicalKey(key) == "url" && next.Contains("://", StringComparison.Ordinal)))
        {
            return (key, null);
        }

        index++;
        return (key, next);
    }

    private static bool LooksLikeKeyToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var t = token.Trim();
        return t.StartsWith("--", StringComparison.Ordinal)
               || t.StartsWith("/", StringComparison.Ordinal)
               || t.StartsWith("?", StringComparison.Ordinal)
               || t.Contains('=');
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

    private static string CanonicalKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(key.Length);
        foreach (var c in key.Trim())
        {
            if (c is '-' or '_')
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static bool ParseBool(string? value, bool defaultWhenMissing)
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

    private static double[]? ParseFiniteList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryParseDouble(parts[i], out values[i]) || !double.IsFinite(values[i]))
            {
                return null;
            }
        }

        return values;
    }

    private static bool TryParseDouble(string? value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseInt(string? value, out int result)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static double Clamp(double value, double? min, double? max)
    {
        if (min.HasValue && value < min.Value)
        {
            return min.Value;
        }

        if (max.HasValue && value > max.Value)
        {
            return max.Value;
        }

        return value;
    }

    private static double Clamp01(double value)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > 1)
        {
            return 1;
        }

        return value;
    }

    private static double[] HslToRgb(double h, double s, double l)
    {
        h = ((h % 1) + 1) % 1;
        s = Clamp01(s);
        l = Clamp01(l);

        if (s == 0)
        {
            return [l, l, l];
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return
        [
            HueToRgb(p, q, h + 1.0 / 3.0),
            HueToRgb(p, q, h),
            HueToRgb(p, q, h - 1.0 / 3.0),
        ];
    }

    private static double HueToRgb(double p, double q, double t)
    {
        t = ((t % 1) + 1) % 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

    private static string Unescape(string value)
    {
        return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
    }

    private static bool IsMatrixDesktopExecutable(string token)
    {
        var fileName = Path.GetFileName(token.Trim('"'));
        return string.Equals(fileName, "MatrixDesktop.exe", StringComparison.OrdinalIgnoreCase)
               || string.Equals(fileName, "MatrixDesktop", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExecutable(string token)
    {
        return token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStartOption(string token)
    {
        return token.StartsWith("/", StringComparison.Ordinal) && !token.Contains('=', StringComparison.Ordinal);
    }

    private static bool IsLauncherCommand(string token)
    {
        return LooksLikeExecutable(token)
               || token.StartsWith("%", StringComparison.Ordinal)
               || token.StartsWith("$", StringComparison.Ordinal);
    }

    public static bool IsStripeEffect(string? effect)
    {
        return !string.IsNullOrWhiteSpace(effect) && StripeEffects.Contains(effect);
    }
}

internal sealed class ImportResult
{
    public JsonObject Draft { get; init; } = [];
    public string[] Applied { get; init; } = [];
    public string[] Ignored { get; init; } = [];
}
