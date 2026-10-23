using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MatrixDesktopConfigurator;

internal static class ZMatrixCfgImporter
{
    private static readonly Dictionary<string, string> GeneralKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MaxStream"] = "zmMaxStream", ["SpeedVariance"] = "zmSpeedVariance",
        ["RefreshTime"] = "zmRefreshTime", ["MonotonousCleanupEnabled"] = "zmMonotonousCleanupEnabled",
        ["BackTrace"] = "zmBackTrace", ["RandomizedCleanupEnabled"] = "zmRandomizedCleanupEnabled",
        ["Leading"] = "zmLeading", ["SpacePad"] = "zmSpacePad",
        ["SpecialStringStreamProbability"] = "zmSpecialStringProbability",
    };
    private static readonly Dictionary<string, string> FontKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FontHeight"] = "height", ["FontWidth"] = "width", ["FontEscapement"] = "escapement",
        ["FontOrientation"] = "orientation", ["FontWeight"] = "weight", ["FontItalic"] = "italic",
        ["FontUnderline"] = "underline", ["FontStrikeOut"] = "strikeOut", ["FontCharSet"] = "charSet",
        ["FontOutPrecision"] = "outPrecision", ["FontClipPrecision"] = "clipPrecision",
        ["FontQuality"] = "quality", ["FontPitch"] = "pitch", ["FontFamily"] = "family", ["FontName"] = "name",
    };
    private static readonly Dictionary<string, int> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ANSI_CHARSET"] = 0, ["DEFAULT_CHARSET"] = 1, ["SYMBOL_CHARSET"] = 2, ["MAC_CHARSET"] = 77,
        ["SHIFTJIS_CHARSET"] = 128, ["HANGEUL_CHARSET"] = 129, ["HANGUL_CHARSET"] = 129,
        ["JOHAB_CHARSET"] = 130, ["GB2312_CHARSET"] = 134, ["CHINESEBIG5_CHARSET"] = 136,
        ["GREEK_CHARSET"] = 161, ["TURKISH_CHARSET"] = 162, ["VIETNAMESE_CHARSET"] = 163,
        ["HEBREW_CHARSET"] = 177, ["ARABIC_CHARSET"] = 178, ["BALTIC_CHARSET"] = 186,
        ["RUSSIAN_CHARSET"] = 204, ["THAI_CHARSET"] = 222, ["EASTEUROPE_CHARSET"] = 238, ["OEM_CHARSET"] = 255,
        ["OUT_DEFAULT_PRECIS"] = 0, ["OUT_STRING_PRECIS"] = 1, ["OUT_CHARACTER_PRECIS"] = 2,
        ["OUT_STROKE_PRECIS"] = 3, ["OUT_TT_PRECIS"] = 4, ["OUT_DEVICE_PRECIS"] = 5,
        ["OUT_RASTER_PRECIS"] = 6, ["OUT_TT_ONLY_PRECIS"] = 7, ["OUT_OUTLINE_PRECIS"] = 8,
        ["OUT_SCREEN_OUTLINE_PRECIS"] = 9, ["OUT_PS_ONLY_PRECIS"] = 10,
        ["CLIP_DEFAULT_PRECIS"] = 0, ["CLIP_CHARACTER_PRECIS"] = 1, ["CLIP_STROKE_PRECIS"] = 2,
        ["CLIP_MASK"] = 15, ["CLIP_LH_ANGLES"] = 16, ["CLIP_TT_ALWAYS"] = 32, ["CLIP_DFA_DISABLE"] = 64, ["CLIP_EMBEDDED"] = 128,
        ["DEFAULT_QUALITY"] = 0, ["DRAFT_QUALITY"] = 1, ["PROOF_QUALITY"] = 2,
        ["NONANTIALIASED_QUALITY"] = 3, ["ANTIALIASED_QUALITY"] = 4, ["CLEARTYPE_QUALITY"] = 5, ["CLEARTYPE_NATURAL_QUALITY"] = 6,
        ["DEFAULT_PITCH"] = 0, ["FIXED_PITCH"] = 1, ["VARIABLE_PITCH"] = 2,
        ["FF_DONTCARE"] = 0, ["FF_ROMAN"] = 16, ["FF_SWISS"] = 32, ["FF_MODERN"] = 48, ["FF_SCRIPT"] = 64, ["FF_DECORATIVE"] = 80,
        ["FW_DONTCARE"] = 0, ["FW_THIN"] = 100, ["FW_EXTRALIGHT"] = 200, ["FW_ULTRALIGHT"] = 200,
        ["FW_LIGHT"] = 300, ["FW_NORMAL"] = 400, ["FW_REGULAR"] = 400, ["FW_MEDIUM"] = 500,
        ["FW_SEMIBOLD"] = 600, ["FW_DEMIBOLD"] = 600, ["FW_BOLD"] = 700,
        ["FW_EXTRABOLD"] = 800, ["FW_ULTRABOLD"] = 800, ["FW_HEAVY"] = 900, ["FW_BLACK"] = 900,
    };
    internal static bool IsConfiguration(string text)
        => Regex.IsMatch(text, @"(?m)^\s*\[(General|Text|Colors|SpecialText)\]\s*\r?$", RegexOptions.IgnoreCase);

    internal static ImportResult Import(string text, JsonObject previous, ConfiguratorMetadata metadata)
    {
        var builder = new CommandBuilder(metadata);
        var draft = builder.CreateSceneDefaultDraft("zmatrix");
        // Importing visual settings must retain the launch mode the user chose.
        foreach (var field in metadata.Groups.SelectMany(group => group.Fields).Where(field => field.Scope == "app"))
            if (previous.TryGetPropertyValue(field.Id, out var value)) draft[field.Id] = value?.DeepClone();
        var fields = metadata.Groups.SelectMany(group => group.Fields).ToDictionary(field => field.Id, StringComparer.OrdinalIgnoreCase);
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scene" };
        var ignored = new List<string>();
        var notes = new List<string>();
        var retained = new JsonObject();
        var fonts = new Dictionary<string, JsonObject> { ["Text"] = ZMatrixControlProfile.FontDefault(), ["SpecialText"] = ZMatrixControlProfile.FontDefault(true) };
        var fontTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        var configDpi = 96;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\ufeff');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            var separator = line.IndexOf('=');
            if (separator < 1) { ignored.Add(section + ": " + line); continue; }
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            var fullKey = section + "." + key;
            retained[fullKey] = value;
            var handled = false;

            if (section.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                if (GeneralKeys.TryGetValue(key, out var id))
                {
                    var parsed = ArgumentImporter.ParseFieldValue(fields[id], value, false);
                    if (parsed is not null) { draft[id] = parsed; applied.Add(id); handled = true; }
                }
                else if (key.Equals("PriorityClass", StringComparison.OrdinalIgnoreCase))
                {
                    var priority = value.ToUpperInvariant() switch
                    {
                        "IDLE_PRIORITY_CLASS" => "idle", "BELOW_NORMAL_PRIORITY_CLASS" => "below-normal",
                        "NORMAL_PRIORITY_CLASS" => "normal", "ABOVE_NORMAL_PRIORITY_CLASS" => "above-normal",
                        "HIGH_PRIORITY_CLASS" => "high", "REALTIME_PRIORITY_CLASS" => "realtime", _ => null,
                    };
                    if (priority is not null)
                    {
                        if (previous["processPriority"]?.GetValue<string>() is null or "auto") { draft["processPriority"] = priority; applied.Add("processPriority"); }
                        else notes.Add(fullKey + " retained; the explicitly selected process priority was preserved.");
                        handled = true;
                    }
                }
                else if (key.Equals("ConfigFileFormatVersion", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(fullKey + " retained; the original loader does not use this version marker.");
                    handled = true;
                }
                else if (key.Equals("ConfigDPI", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dpi))
                    {
                        configDpi = Math.Clamp(dpi, 48, 768);
                        notes.Add(fullKey + " retained; font height and width are converted to 96-DPI logical units.");
                        handled = true;
                    }
                }
            }
            else if (section.Equals("Text", StringComparison.OrdinalIgnoreCase) || section.Equals("SpecialText", StringComparison.OrdinalIgnoreCase))
            {
                var fontSection = section.Equals("Text", StringComparison.OrdinalIgnoreCase) ? "Text" : "SpecialText";
                if (FontKeys.TryGetValue(key, out var fontKey))
                {
                    JsonNode? node = null;
                    if (fontKey == "name") node = JsonValue.Create(Unquote(value));
                    else if (fontKey is "italic" or "underline" or "strikeOut") node = JsonValue.Create(MatrixDesktop.Shared.FlagNormalization.ParseBool(value, false));
                    else if (TryConstant(value, out var number)) node = JsonValue.Create(number);
                    if (node is not null)
                    {
                        var candidate = (JsonObject)fonts[fontSection].DeepClone();
                        candidate[fontKey] = node;
                        var normalized = ZMatrixControlProfile.NormalizeFont(candidate, fontSection == "SpecialText");
                        if (normalized is not null)
                        {
                            // Preserve source device dimensions until ConfigDPI is known,
                            // regardless of which section appeared first in the file.
                            normalized["height"] = candidate["height"]!.DeepClone();
                            normalized["width"] = candidate["width"]!.DeepClone();
                            fonts[fontSection] = normalized; fontTouched.Add(fontSection); handled = true;
                        }
                    }
                }
                else if (fontSection == "Text" && key.Equals("CharSet", StringComparison.OrdinalIgnoreCase))
                {
                    var characters = ParseCharacters(value);
                    var parsed = ZMatrixControlProfile.ParseStructured(fields["zmCharacters"], characters.ToJsonString());
                    if (parsed is not null) { draft["zmCharacters"] = parsed; applied.Add("zmCharacters"); handled = true; }
                }
                else if (fontSection == "SpecialText" && key.Equals("Strings", StringComparison.OrdinalIgnoreCase))
                {
                    var phrases = new JsonArray(value.Replace("\\n", "|", StringComparison.Ordinal).Split(['|', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());
                    var parsed = ZMatrixControlProfile.ParseStructured(fields["zmPhrases"], phrases.ToJsonString());
                    if (parsed is not null) { draft["zmPhrases"] = parsed; applied.Add("zmPhrases"); handled = true; }
                }
            }
            else if (section.Equals("Colors", StringComparison.OrdinalIgnoreCase))
            {
                var colorId = key.ToLowerInvariant() switch
                {
                    "fgcolor" => "zmForegroundColor", "fadecolor" => "zmFadeColor", "bgcolor" => "zmBackgroundColor",
                    "specialstringfgcolor" => "zmSpecialForegroundColor", "specialstringfadecolor" => "zmSpecialFadeColor", _ => null,
                };
                if (colorId is not null)
                {
                    var values = value.Trim('{', '}', ' ').Split(',');
                    var parsed = values.Length is 3 or 4 ? ZMatrixControlProfile.ParseRgb(string.Join(',', values.Take(3))) : null;
                    if (parsed is not null)
                    {
                        draft[colorId] = parsed; applied.Add(colorId); handled = true;
                        if (values.Length == 4 && double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha) && double.IsFinite(alpha))
                        {
                            if (colorId == "zmBackgroundColor") { draft["zmOpaqueBackground"] = alpha >= 128; applied.Add("zmOpaqueBackground"); }
                            else notes.Add(fullKey + " alpha retained; the original renderer ignores it.");
                        }
                    }
                }
                else if (key.Equals("BGMode", StringComparison.OrdinalIgnoreCase))
                {
                    var mode = value.ToLowerInvariant() switch { "bgmodebitmap" => "bitmap", "bgmodecolor" => "color", _ => null };
                    if (mode is not null) { draft["zmBackgroundMode"] = mode; applied.Add("zmBackgroundMode"); handled = true; }
                }
                else if (key.Equals("BlendMode", StringComparison.OrdinalIgnoreCase))
                {
                    var mode = value.ToLowerInvariant() switch { "blendmodeor" => "or", "blendmodexor" => "xor", "blendmodeand" => "and", _ => null };
                    if (mode is not null) { draft["zmBlendMode"] = mode; applied.Add("zmBlendMode"); handled = true; }
                }
                else if (key.Equals("SpecialStringBGColor", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(fullKey + " retained; the original renderer does not use the special background color.");
                    handled = true;
                }
            }
            if (!handled) { ignored.Add(fullKey); notes.Add(fullKey + " retained without an active control (unknown, invalid or unused setting)."); }
        }
        if (configDpi != 96)
        {
            fontTouched.UnionWith(["Text", "SpecialText"]);
            notes.Add("Cleanup backtrace, leading and padding retain glyph counts; the original extra DPI scaling of these counts is corrected.");
        }
        foreach (var fontSection in fontTouched)
        {
            var id = fontSection == "Text" ? "zmFont" : "zmSpecialFont";
            foreach (var dimension in new[] { "height", "width" })
                fonts[fontSection][dimension] = Math.Round(fonts[fontSection][dimension]!.GetValue<int>() * 96.0 / configDpi, MidpointRounding.AwayFromZero);
            draft[id] = ZMatrixControlProfile.NormalizeFont(fonts[fontSection], fontSection == "SpecialText");
            applied.Add(id);
        }
        draft["zmImportedSettings"] = retained;
        draft["zmImportNotes"] = new JsonArray(notes.Distinct(StringComparer.Ordinal).Select(note => (JsonNode?)JsonValue.Create(note)).ToArray());
        return new ImportResult { Draft = draft, Applied = applied.Order(StringComparer.OrdinalIgnoreCase).ToArray(), Ignored = ignored.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Notes = notes.Distinct(StringComparer.Ordinal).ToArray() };
    }

    private static bool TryConstant(string text, out int value)
    {
        value = 0;
        foreach (var part in text.Split('|', StringSplitOptions.TrimEntries))
        {
            if (Constants.TryGetValue(part, out var named)) value |= named;
            else if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(part.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) value |= hex;
            else if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) value |= number;
            else return false;
        }
        return true;
    }

    private static JsonArray ParseCharacters(string value)
    {
        var characters = new List<string>();
        foreach (var raw in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var token = Unquote(raw);
            if (token.Length == 0) continue;
            if (token.Length == 1) characters.Add(token);
            else if (TryBaseZero(token, out var number) && number is >= 0 and <= 0xffff) characters.Add(((char)number).ToString());
            else characters.Add(token[..1]);
        }
        return new JsonArray(characters.Select(character => (JsonNode?)JsonValue.Create(character)).ToArray());
    }

    private static bool TryBaseZero(string value, out int number)
    {
        number = 0;
        try
        {
            number = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(value[2..], 16)
                : value.Length > 1 && value[0] == '0' ? Convert.ToInt32(value, 8) : Convert.ToInt32(value, 10);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException) { return false; }
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"' ? value[1..^1] : value;
}
