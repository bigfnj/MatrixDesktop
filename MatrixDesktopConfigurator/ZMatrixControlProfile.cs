using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatrixDesktopConfigurator;

// The ZMatrix scene has independent units. Keeping its fields together also makes
// scene filtering and CFG conversion use the same catalogue as command generation.
internal static class ZMatrixControlProfile
{
    internal const string DefaultCharacters = "1234567890ABCDEFGHIJKLMNOPQRSUTVWXYZabcd";
    internal static readonly HashSet<string> CommonKeys = new(StringComparer.OrdinalIgnoreCase)
        { "scene", "renderer", "resolution", "fps", "once", "suppressWarnings" };
    internal static readonly (string Id, string Label, double Min, double Max, double Default)[] FontNumbers =
    [
        ("height", "Height (logical pixels)", -256, 256, -7),
        ("width", "Width (0 = automatic)", 0, 256, 0),
        ("escapement", "Text angle (tenths of a degree)", -3600, 3600, 0),
        ("orientation", "Character angle (tenths of a degree)", -3600, 3600, 0),
        ("weight", "Weight", 0, 1000, 400),
        ("charSet", "Windows character set", 0, 255, 1),
        ("outPrecision", "Output precision", 0, 255, 0),
        ("clipPrecision", "Clipping precision", 0, 255, 0),
        ("quality", "Raster quality", 0, 255, 0),
        ("pitch", "Pitch", 0, 3, 0),
        ("family", "Family", 0, 240, 0),
    ];

    internal static JsonObject FontDefault(bool special = false) => new()
    {
        ["height"] = special ? -8 : -7, ["width"] = 0, ["escapement"] = 0,
        ["orientation"] = 0, ["weight"] = 400, ["italic"] = false,
        ["underline"] = false, ["strikeOut"] = false, ["charSet"] = 1,
        ["outPrecision"] = 0, ["clipPrecision"] = 0, ["quality"] = 0,
        ["pitch"] = 0, ["family"] = 0, ["name"] = special ? "Terminal" : "Matrix Code Font",
    };

    internal static IEnumerable<ArgumentGroup> CreateGroups()
    {
        yield return new ArgumentGroup
        {
            Id = "zmatrix", Title = "ZMatrix", Fields =
            [
                Integer("zmMaxStream", "Maximum streams", 1000, 1, 4096, "Maximum simultaneously allocated streams."),
                Integer("zmSpeedVariance", "Speed variance", 5, 0, 120, "Random per-stream speed variation in original units."),
                Integer("zmRefreshTime", "Refresh interval (milliseconds)", 50, 10, 1000, "Simulation interval; independent of the presentation FPS limit."),
                Toggle("zmMonotonousCleanupEnabled", "Monotonous cleanup", false),
                Integer("zmBackTrace", "Fixed cleanup distance", 40, 0, 4096, "Monotonous cleanup distance in glyph units (BackTrace)."),
                Toggle("zmRandomizedCleanupEnabled", "Randomized cleanup", false),
                Integer("zmLeading", "Minimum cleanup distance", 10, 0, 4096, "Minimum randomized cleanup distance in glyph units (Leading)."),
                Integer("zmSpacePad", "Extra random cleanup distance", 30, 0, 4096, "Additional randomized cleanup distance in glyph units (SpacePad)."),
                Number("zmSpecialStringProbability", "Phrase stream probability", .1, 0, 1, .01),
                new() { Id = "zmCharacters", ArgName = "zmCharacters", Label = "Characters", Scope = "web", Kind = "characters", DefaultValue = DefaultCharacters.Select(c => c.ToString()).ToArray(), Help = "Each character is one available glyph. Up to 512 distinct BMP characters; spaces are significant." },
                new() { Id = "zmPhrases", ArgName = "zmPhrases", Label = "Phrase streams", Scope = "web", Kind = "phrases", DefaultValue = new[] { "The matrix has you" }, Help = "One phrase per entry. Up to 64 phrases of 256 BMP characters each." },
            ],
        };
        yield return new ArgumentGroup
        {
            Id = "zmatrixAppearance", Title = "ZMatrix appearance", Fields =
            [
                Choice("zmBackgroundMode", "Background mode", "bitmap", ("bitmap", "Desktop wallpaper"), ("color", "Solid color")),
                Choice("zmBlendMode", "Wallpaper blend", "or", ("or", "OR"), ("xor", "XOR"), ("and", "AND")),
                Rgb("zmForegroundColor", "Code foreground", 187, 255, 187),
                Rgb("zmFadeColor", "Code fade", 0, 128, 0),
                Rgb("zmBackgroundColor", "Code background", 0, 0, 0),
                Toggle("zmOpaqueBackground", "Opaque character background", false),
                Rgb("zmSpecialForegroundColor", "Phrase foreground", 251, 255, 251),
                Rgb("zmSpecialFadeColor", "Phrase fade", 208, 244, 208),
                new() { Id = "zmFont", ArgName = "zmFont", Label = "Code font", Scope = "web", Kind = "font", DefaultValue = FontDefault(), Help = "Windows LOGFONT settings. Missing fonts report the resolved fallback in the preview." },
                new() { Id = "zmSpecialFont", ArgName = "zmSpecialFont", Label = "Phrase font", Scope = "web", Kind = "font", DefaultValue = FontDefault(true), Help = "Independent Windows font settings for phrase streams." },
            ],
        };
        yield return new ArgumentGroup
        {
            Id = "zmatrixAdvanced", Title = "ZMatrix color transforms", Fields =
            [
                Number("zmRedGain", "Red gain", 1, -4, 4, .01),
                Number("zmGreenGain", "Green gain", 1, -4, 4, .01),
                Number("zmBlueGain", "Blue gain", 1, -4, 4, .01),
                Number("zmRedOffset", "Red offset", 0, -255, 255, 1),
                Number("zmGreenOffset", "Green offset", 0, -255, 255, 1),
                Number("zmBlueOffset", "Blue offset", 0, -255, 255, 1),
            ],
        };
    }

    internal static bool IsZMatrix(JsonObject draft) => draft["scene"] is JsonValue value && value.TryGetValue<string>(out var scene) && scene == "zmatrix";
    internal static List<ArgumentDefinition> FontEditorFields() => FontNumbers
        .Select(field => Number(field.Id, field.Label, field.Default, field.Min, field.Max, 1))
        .Concat(new[]
        {
            Toggle("italic", "Italic", false), Toggle("underline", "Underline", false), Toggle("strikeOut", "Strikeout", false),
            new ArgumentDefinition { Id = "name", ArgName = "name", Label = "Font family", Scope = "web", Kind = "text", DefaultValue = "Matrix Code Font" },
        }).ToList();
    internal static bool IsActive(string key, JsonObject draft)
        => IsZMatrix(draft) ? key.StartsWith("zm", StringComparison.Ordinal) || CommonKeys.Contains(key) : !key.StartsWith("zm", StringComparison.Ordinal);

    internal static JsonNode? ParseStructured(ArgumentDefinition field, string? text)
    {
        if (text is null) return null;
        if (!global::Shared.StructuredArgumentCodec.TryDecode(text, out text)) return null;
        try
        {
            if (field.Kind == "rgb8") return ParseRgb(text);
            var node = JsonNode.Parse(text);
            if (field.Kind == "font") return NormalizeFont(node, field.Id == "zmSpecialFont");
            if (node is not JsonArray list || list.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _))) return null;
            var strings = list.Select(item => item!.GetValue<string>()).ToArray();
            if (field.Kind == "characters")
            {
                if (strings.Length is < 1 or > 512 || strings.Any(value => value.Length != 1 || value.Any(c => char.IsSurrogate(c) || char.IsControl(c)))) return null;
                return new JsonArray(strings.Distinct(StringComparer.Ordinal).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            }
            if (strings.Length > 64 || strings.Any(value => value.Length is < 1 or > 256 || value.Any(c => char.IsSurrogate(c) || char.IsControl(c))) || strings.SelectMany(value => value).Distinct().Count() > 1024) return null;
            return new JsonArray(strings.Distinct(StringComparer.Ordinal).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        }
        catch (JsonException) { return null; }
    }

    internal static JsonObject? NormalizeFont(JsonNode? value, bool special)
    {
        if (value is not JsonObject input) return null;
        var result = FontDefault(special);
        foreach (var (id, _, min, max, _) in FontNumbers)
        {
            if (!input.TryGetPropertyValue(id, out var node)) continue;
            if (!TryNumber(node, out var n) || !double.IsFinite(n)) return null;
            result[id] = (int)Math.Clamp(Math.Truncate(n), min, max);
        }
        result["family"] = result["family"]!.GetValue<int>() & 0xf0;
        foreach (var id in new[] { "italic", "underline", "strikeOut" })
        {
            if (!input.TryGetPropertyValue(id, out var node)) continue;
            if (node is not JsonValue flag || !flag.TryGetValue<bool>(out var enabled)) return null;
            result[id] = enabled;
        }
        if (input.TryGetPropertyValue("name", out var nameNode))
        {
            if (nameNode is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name) || name.Length > 31 || name.Any(c => char.IsControl(c) || char.IsSurrogate(c))) return null;
            result["name"] = name.Trim();
        }
        return result;
    }

    internal static JsonObject? ParseRgb(string text)
    {
        if (text.Length == 7 && text[0] == '#' && int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return RgbNode((hex >> 16) & 255, (hex >> 8) & 255, hex & 255);
        var parts = text.Trim().Trim('{', '}', '[', ']').Split(',');
        if (parts.Length != 3) return null;
        var values = new double[3];
        for (var i = 0; i < 3; i++)
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i])) return null;
        return RgbNode(values[0], values[1], values[2]);
    }

    internal static JsonObject RgbNode(double r, double g, double b) => new()
        { ["r"] = Math.Clamp(Math.Round(r, MidpointRounding.AwayFromZero), 0, 255), ["g"] = Math.Clamp(Math.Round(g, MidpointRounding.AwayFromZero), 0, 255), ["b"] = Math.Clamp(Math.Round(b, MidpointRounding.AwayFromZero), 0, 255) };
    private static bool TryNumber(JsonNode? node, out double number)
    {
        number = 0;
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<double>(out number)) return true;
        if (!value.TryGetValue<int>(out var integer)) return false;
        number = integer;
        return true;
    }
    private static ArgumentDefinition Integer(string id, string label, double value, double min, double max, string help)
        => new() { Id = id, ArgName = id, Label = label, Scope = "web", Kind = "number", DefaultValue = value, Min = min, Max = max, Step = 1, Slider = true, Help = help };
    private static ArgumentDefinition Number(string id, string label, double value, double min, double max, double step)
        => new() { Id = id, ArgName = id, Label = label, Scope = "web", Kind = "number", DefaultValue = value, Min = min, Max = max, Step = step };
    private static ArgumentDefinition Toggle(string id, string label, bool value)
        => new() { Id = id, ArgName = id, Label = label, Scope = "web", Kind = "bool", DefaultValue = value, TrueFlag = id };
    private static ArgumentDefinition Rgb(string id, string label, int r, int g, int b)
        => new() { Id = id, ArgName = id, Label = label, Scope = "web", Kind = "rgb8", DefaultValue = RgbNode(r, g, b) };
    private static ArgumentDefinition Choice(string id, string label, string value, params (string Value, string Label)[] options)
        => new() { Id = id, ArgName = id, Label = label, Scope = "web", Kind = "select", DefaultValue = value, Options = options.Select(option => new ArgumentOption { Value = option.Value, Label = option.Label }).ToList() };
}
