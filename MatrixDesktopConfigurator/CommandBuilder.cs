using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatrixDesktopConfigurator;

internal sealed partial class CommandBuilder
{
    private readonly ConfiguratorMetadata _metadata;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions StructuredJsonOptions = new(JsonSerializerDefaults.Web)
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public CommandBuilder(ConfiguratorMetadata metadata)
    {
        _metadata = metadata;
    }

    internal ConfiguratorMetadata Metadata => _metadata;

    public JsonObject CreateDefaultDraft()
    {
        var draft = new JsonObject();
        foreach (var field in Fields)
        {
            draft[field.Id] = JsonSerializer.SerializeToNode(field.DefaultValue, _jsonOptions);
        }

        return draft;
    }

    public JsonObject CreateSceneDefaultDraft(string scene)
    {
        var draft = CreateDefaultDraft();
        draft["scene"] = scene;
        if (_metadata.SceneDefaults.TryGetValue(scene, out var defaults))
        {
            foreach (var (key, value) in defaults)
                draft[key] = JsonSerializer.SerializeToNode(value, _jsonOptions);
        }
        return draft;
    }

    public string BuildCommand(JsonObject values, bool includeDefaults, bool forTest)
    {
        var command = string.Join(" ", BuildTokens(values, includeDefaults, forTest, encodeStructured: true).Select(QuoteToken));
        if (command.Length >= 32_767) throw new InvalidOperationException("The copied command exceeds Windows' 32,767-character limit. Reduce the phrase list, or use Test Argument / Export .ps1 with direct structured arguments.");
        return command;
    }

    public IReadOnlyList<string> BuildArguments(JsonObject values, bool includeDefaults, bool forTest)
    {
        var tokens = BuildTokens(values, includeDefaults, forTest);
        if (tokens.Sum(token => QuoteToken(token).Length + 1) >= 32_767) throw new InvalidOperationException("The launch arguments exceed Windows' 32,767-character limit. Reduce the phrase list or character set.");
        return tokens.Skip(1).ToArray();
    }

    public string BuildTestCommandDescription(JsonObject values)
    {
        _ = BuildArguments(values, includeDefaults: false, forTest: true);
        try { return BuildCommand(values, includeDefaults: false, forTest: true); }
        catch (InvalidOperationException)
        {
            return "MatrixDesktop.exe --windowed (direct structured launch; use Export .ps1 to reproduce this large configuration)";
        }
    }

    private List<string> BuildTokens(JsonObject values, bool includeDefaults, bool forTest, bool encodeStructured = false)
    {
        var tokens = new List<string> { "MatrixDesktop.exe" };
        AddAppTokens(tokens, values, includeDefaults, forTest);
        AddWebTokens(tokens, values, includeDefaults, encodeStructured);
        return tokens;
    }

    private void AddAppTokens(List<string> tokens, JsonObject values, bool includeDefaults, bool forTest)
    {
        var windowMode = GetString(values, "windowMode", "borderless");
        var monitor = GetNullableNumber(values, "monitor");

        if (forTest)
        {
            tokens.Add("--windowed");
        }
        else if (windowMode == "single-monitor" && monitor.HasValue)
        {
            tokens.Add("--monitor");
            tokens.Add(Math.Max(0, (int)Math.Round(monitor.Value)).ToString(CultureInfo.InvariantCulture));
        }
        else if (windowMode == "windowed")
        {
            tokens.Add("--windowed");
        }
        else if (windowMode == "wallpaper")
        {
            tokens.Add("--wallpaper");
        }
        else if (windowMode == "single-monitor")
        {
            tokens.Add("--single-monitor");
        }
        else if (includeDefaults)
        {
            tokens.Add("--borderless");
        }

        foreach (var field in Fields.Where(static f => f.Scope == "app" && f.Kind == "bool"))
        {
            if (field.Id == "workingArea" && (forTest || windowMode is "windowed" or "wallpaper"))
            {
                continue;
            }
            if (field.Id.StartsWith("wallpaper", StringComparison.Ordinal) && (forTest || windowMode != "wallpaper")) continue;
            if (windowMode == "wallpaper" && !forTest && field.Id is "topmost" or "hideCursor" or "exitOnAnyKey" or "exitOnEsc" or "globalKeyExit") continue;

            // A quick windowed test shouldn't cover other windows or hide the
            // cursor — those make the test window awkward to inspect and close.
            if (forTest && (field.Id == "topmost" || field.Id == "hideCursor"))
            {
                continue;
            }

            var value = GetBool(values, field.Id, field.DefaultValue is bool b && b);
            var defaultValue = field.DefaultValue is bool db && db;
            var shouldEmit = includeDefaults || value != defaultValue || (forTest && field.Id == "exitOnAnyKey");

            if (!shouldEmit)
            {
                continue;
            }

            if (field.Id == "exitOnAnyKey" && forTest)
            {
                tokens.Add("--no-exit-on-any-key");
                continue;
            }

            var flag = value ? field.TrueFlag : field.FalseFlag;
            if (!string.IsNullOrWhiteSpace(flag))
            {
                tokens.Add(flag);
            }
        }
        var priority = GetString(values, "processPriority", "auto");
        if (!forTest && (includeDefaults || priority != "auto"))
        {
            tokens.Add("--process-priority");
            tokens.Add(priority);
        }
    }

    private void AddWebTokens(List<string> tokens, JsonObject values, bool includeDefaults, bool encodeStructured)
    {
        var effect = GetString(values, "effect", "palette");
        foreach (var field in Fields.Where(static f => f.Scope == "web"))
        {
            if (!Uc3dControlProfile.IsActive(field.Id, values) || !ZMatrixControlProfile.IsActive(field.Id, values)) continue;
            if (IsInactiveForEffect(field.Id, effect))
            {
                continue;
            }

            var value = field.Id == "uc3dControls" && Uc3dControlProfile.IsUc3d(values)
                ? JsonValue.Create(Uc3dControlProfile.Mode(values)) : values[field.Id];
            if (value is null)
            {
                continue;
            }

            if (!includeDefaults && IsDefault(field, value, values))
            {
                continue;
            }

            var serialized = SerializeWebValue(field, value);
            if (encodeStructured && field.Kind is "font" or "characters" or "phrases")
                serialized = global::Shared.StructuredArgumentCodec.Encode(serialized);
            else if (encodeStructured && field.Kind == "text" && global::Shared.StructuredArgumentCodec.TextNeedsEncoding(serialized))
                serialized = global::Shared.StructuredArgumentCodec.EncodeText(serialized);
            if (string.IsNullOrWhiteSpace(serialized))
            {
                continue;
            }

            tokens.Add($"--{field.ArgName}");
            tokens.Add(serialized);
        }
    }

    // Use for screensaver saves what the preview and Test render. It used to emit every
    // default, which wrote the catalog's stripe rows and base values over each version's own.
    public IReadOnlyList<string> BuildScreensaverArguments(JsonObject values) => BuildArguments(values, includeDefaults: false, forTest: false);

    // AUD-13: a value equal to the old scene-or-catalog default was left out, so it rendered the
    // version's or effect's own value instead; replacing it with that rendered value keeps the
    // render and lets the editor show it. Runs once over a stored draft (PresetSeeder, schema 2)
    // and on every import for the fields the command did not name, never over fresh edits, so a
    // deliberately chosen base colour stays chosen. Inactive fields keep their value.
    internal static bool AdoptRenderedDefaults(ConfiguratorMetadata metadata, JsonObject draft, ICollection<string>? explicitKeys = null)
    {
        var scene = GetString(draft, "scene", "matrix");
        var uc3dMode = Uc3dControlProfile.Mode(draft);
        var version = GetString(draft, "version", "classic");
        var effect = GetString(draft, "effect", "palette");
        var changed = false;
        foreach (var field in metadata.Groups.SelectMany(static g => g.Fields).Where(static f => f.Scope == "web"))
        {
            if (explicitKeys?.Contains(field.Id) == true || draft[field.Id] is not { } value) continue;
            if (!Uc3dControlProfile.IsActive(field.Id, draft) || !ZMatrixControlProfile.IsActive(field.Id, draft) || IsInactiveForEffect(field.Id, effect)) continue;
            var rendered = JsonSerializer.SerializeToNode(metadata.EffectiveDefault(field, scene, uc3dMode, version, effect), NormalizeOptions);
            var legacy = JsonSerializer.SerializeToNode(metadata.LegacyDefault(field, scene, uc3dMode), NormalizeOptions);
            if (rendered is null || legacy is null || NormalizeJson(rendered) == NormalizeJson(legacy) || NormalizeJson(value) != NormalizeJson(legacy)) continue;
            draft[field.Id] = rendered;
            changed = true;
        }
        return changed;
    }

    // A value the selected effect cannot use stays out of every command and the preview, as
    // app.js getDisabledReason greys its editor, while the saved draft keeps it so returning
    // to that effect restores it. Camera matters most: both renderers open the webcam
    // whenever camera is true, and the page will not let a greyed checkbox be cleared.
    // Effect names compare as the renderers look them up (regl/main.js effects[name]).
    internal static bool IsInactiveForEffect(string fieldId, string effect) => fieldId switch
    {
        "stripeColors" => !ArgumentImporter.IsStripeEffect(effect),
        "camera" => !string.Equals(effect, "mirror", StringComparison.Ordinal),
        _ => false,
    };

    private string SerializeWebValue(ArgumentDefinition field, JsonNode value)
    {
        return field.Kind switch
        {
            "bool" => GetBoolValue(value) ? "true" : "false",
            "number" => GetNumberValue(value)?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
            "color" => SerializeColor(value),
            "rgb8" => SerializeColor(value),
            "palette" => SerializePalette(value),
            "stripes" => SerializeStripes(value),
            "font" or "characters" or "phrases" => value.ToJsonString(StructuredJsonOptions),
            _ => value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString(_jsonOptions),
        };
    }

    // MD-30: the serialised form of each field's default is computed once and kept, rather
    // than being re-serialised for every field on every call. BuildCommand and
    // BuildWebQueryString both run on a per-keystroke debounce, and every call walked all
    // ~60 fields calling SerializeToNode plus ToJsonString twice. The catalog is immutable
    // once ArgumentCatalog.Create has run, so caching is safe by construction.
    private readonly Dictionary<string, string?> _defaultJsonByFieldId = [];

    // AUD-13: a value is left out only when it equals what the renderer draws without it for
    // this scene, version and effect, so an editor showing that value shows the render, and any
    // other value, the catalog's base colours included, can be set.
    private bool IsDefault(ArgumentDefinition field, JsonNode value, JsonObject values)
    {
        var scene = GetString(values, "scene", "matrix");
        var uc3dMode = Uc3dControlProfile.Mode(values);
        var version = GetString(values, "version", "classic");
        var effect = GetString(values, "effect", "palette");
        var cacheKey = scene + ":" + uc3dMode + ":" + version + ":" + effect + ":" + field.Id;
        if (!_defaultJsonByFieldId.TryGetValue(cacheKey, out var defaultJson))
        {
            var defaultNode = JsonSerializer.SerializeToNode(_metadata.EffectiveDefault(field, scene, uc3dMode, version, effect), _jsonOptions);
            defaultJson = defaultNode is null ? null : CanonicalJson(defaultNode);
            _defaultJsonByFieldId[cacheKey] = defaultJson;
        }

        if (defaultJson is null)
        {
            return IsEmpty(value);
        }

        return CanonicalJson(value) == defaultJson;
    }

    // R2-44: key order is no difference. A draft saved by the close snapshot holds its colours
    // as b, g, r and its palette stops as at, b, g, r, so comparing serialized text turned every
    // default colour into a flag after a close and reopen. Objects compare with sorted keys.
    private static string CanonicalJson(JsonNode node) => node is JsonObject or JsonArray ? NormalizeJson(Canonical(node)!) : NormalizeJson(node);

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => KeyValuePair.Create(pair.Key, Canonical(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        _ => node?.DeepClone(),
    };

    private bool IsEmpty(JsonNode value)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var s))
        {
            return string.IsNullOrWhiteSpace(s);
        }

        if (value.GetValueKind() == JsonValueKind.Null)
        {
            return true;
        }

        return false;
    }

    private string SerializeColor(JsonNode value)
    {
        if (value is not JsonObject color)
        {
            return string.Empty;
        }

        return JoinNumbers(ReadNumber(color, "r"), ReadNumber(color, "g"), ReadNumber(color, "b"));
    }

    private string SerializePalette(JsonNode value)
    {
        if (value is not JsonArray stops)
        {
            return string.Empty;
        }

        var values = new List<double>();
        foreach (var item in stops)
        {
            if (item is not JsonObject stop)
            {
                continue;
            }

            values.Add(ReadNumber(stop, "r"));
            values.Add(ReadNumber(stop, "g"));
            values.Add(ReadNumber(stop, "b"));
            values.Add(ReadNumber(stop, "at"));
        }

        return values.Count == 0 ? string.Empty : JoinNumbers(values);
    }

    private string SerializeStripes(JsonNode value)
    {
        if (value is not JsonArray colors)
        {
            return string.Empty;
        }

        var values = new List<double>();
        foreach (var item in colors)
        {
            if (item is not JsonObject color)
            {
                continue;
            }

            values.Add(ReadNumber(color, "r"));
            values.Add(ReadNumber(color, "g"));
            values.Add(ReadNumber(color, "b"));
        }

        return values.Count == 0 ? string.Empty : JoinNumbers(values);
    }

    // Shared instance rather than a fresh one per call. JsonSerializerOptions builds and caches
    // a converter set on first use, so allocating one per call threw that cache away every time
    // and re-resolved converters. IsDefaultValue calls this up to twice per field and the
    // configurator rebuilds the command on every keystroke, so at ~60 fields it was on the order
    // of a hundred throwaway option sets per edit. Safe to share: the instance is never mutated
    // after construction, and JsonSerializerOptions is documented as thread-safe once used.
    private static readonly JsonSerializerOptions NormalizeOptions = new(JsonSerializerDefaults.Web);

    private static string NormalizeJson(JsonNode node)
    {
        return node.ToJsonString(NormalizeOptions);
    }

    private static string JoinNumbers(params double[] values) => JoinNumbers((IEnumerable<double>)values);

    private static string JoinNumbers(IEnumerable<double> values)
    {
        return string.Join(",", values.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    private static double ReadNumber(JsonObject obj, string key)
    {
        return obj[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
    }

    private static bool GetBool(JsonObject obj, string key, bool defaultValue)
    {
        var node = obj[key];
        return node is null ? defaultValue : GetBoolValue(node);
    }

    private static bool GetBoolValue(JsonNode value)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var b)) return b;
            if (jsonValue.TryGetValue<string>(out var s)) return s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string GetString(JsonObject obj, string key, string defaultValue)
    {
        var node = obj[key];
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return defaultValue;
    }

    private static double? GetNullableNumber(JsonObject obj, string key)
    {
        var node = obj[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var number)) return number;
            if (value.TryGetValue<string>(out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static double? GetNumberValue(JsonNode value)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<double>(out var number)) return number;
            if (jsonValue.TryGetValue<string>(out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string QuoteToken(string token)
    {
        if (token.Length == 0)
        {
            return "\"\"";
        }

        if (!NeedsQuoting(token))
        {
            return token;
        }

        // Windows command-line escaping: only backslashes before a quote or the
        // closing delimiter are doubled. Ordinary path separators stay literal.
        var result = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in token)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"') result.Append('\\', backslashes * 2 + 1).Append('"');
            else result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    // A copied command may be pasted into cmd.exe or PowerShell, where these
    // characters are shell-significant (an unquoted '&' in an image URL, for
    // instance, would truncate the command). Double quotes keep whitespace, '&',
    // '|', '<', '>', '^' and parentheses literal in both shells, and in PowerShell
    // an apostrophe (which pairs with the next one across arguments), ';' (which
    // ends the statement), braces (a script block), '@' (splatting) and '#' (a
    // comment) (R2-12), and the typographic apostrophes U+2018 to U+201B, which
    // PowerShell reads as apostrophes (R3-07). They do not keep '%' (cmd and batch
    // files expand it), '!' (delayed expansion), '$' or a backtick (PowerShell
    // expands both), or the typographic double quotes U+201C to U+201E, which end a
    // PowerShell double-quoted string. Text values holding those travel as json64
    // instead (AddWebTokens), as structured values always do.
    private static bool NeedsQuoting(string token)
    {
        foreach (var c in token)
        {
            if (char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')' or '%' or '!'
                or '\'' or ';' or '{' or '}' or '@' or '#' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<ArgumentDefinition> Fields => _metadata.Groups.SelectMany(static g => g.Fields);
}

// ─── v1.0 additions to CommandBuilder ──────────────────────────────────────
// Placed in a partial so they can sit logically next to the rest of the
// command-building code without having to crack the existing file open.

internal sealed partial class CommandBuilder
{
    // Build a URL-style query string for the live preview window: ?key=val&key=val
    // Web-only fields are emitted (the app-level flags like --windowed don't
    // apply to the preview, which is always windowed). Values are URL-encoded.
    //
    // Defaults are SKIPPED, matching the actual launch command (AddWebTokens).
    // This matters for correctness, not just size: the matrix app treats the
    // mere presence of some params as an override. e.g. stripePass.js uses the
    // effect's built-in colors (pride = rainbow, trans = flag) only when
    // `stripeColors` is absent — so always emitting the default stripeColors
    // would flatten pride/trans/stripes into the same look.
    public string BuildWebQueryString(JsonObject values)
    {
        var pairs = new List<string>();
        var effect = GetString(values, "effect", "palette");

        foreach (var field in Fields.Where(static f => f.Scope == "web"))
        {
            if (!Uc3dControlProfile.IsActive(field.Id, values) || !ZMatrixControlProfile.IsActive(field.Id, values)) continue;
            if (IsInactiveForEffect(field.Id, effect))
            {
                continue;
            }

            var value = field.Id == "uc3dControls" && Uc3dControlProfile.IsUc3d(values)
                ? JsonValue.Create(Uc3dControlProfile.Mode(values)) : values[field.Id];
            if (value is null) continue;

            if (IsDefault(field, value, values)) continue;

            var serialized = SerializeWebValue(field, value);
            if (string.IsNullOrWhiteSpace(serialized)) continue;

            pairs.Add($"{Uri.EscapeDataString(field.ArgName)}={Uri.EscapeDataString(serialized)}");
        }

        return pairs.Count == 0 ? string.Empty : "?" + string.Join("&", pairs);
    }

    // Build a PowerShell .ps1 script body that launches MatrixDesktop with the
    // current draft. The output uses $PSScriptRoot so the user can drop the
    // .ps1 next to MatrixDesktop.exe and run it directly.
    public string BuildPowerShellScript(JsonObject values, bool includeDefaults)
    {
        var args = BuildArguments(values, includeDefaults, forTest: false);
        var arguments = string.Join(" ", args.Select(QuoteToken));
        // PowerShell 5.1 removes embedded JSON quotes when it binds native
        // arguments. Let Windows parse our quoted raw argument string directly.
        // ASCII transport also avoids PowerShell 5.1's BOM-less UTF-8 ambiguity.
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(arguments));
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# MatrixDesktop launch - generated by MatrixDesktopConfigurator");
        sb.AppendLine("# Save this beside MatrixDesktop.exe and run it.");
        sb.AppendLine();
        sb.AppendLine("$matrixStartInfo = New-Object System.Diagnostics.ProcessStartInfo");
        sb.AppendLine("$matrixStartInfo.FileName = Join-Path $PSScriptRoot 'MatrixDesktop.exe'");
        sb.AppendLine("$matrixStartInfo.WorkingDirectory = $PSScriptRoot");
        sb.AppendLine("$matrixStartInfo.UseShellExecute = $false");
        sb.AppendLine("$matrixStartInfo.CreateNoWindow = $true");
        sb.AppendLine("$matrixStartInfo.Arguments = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('" + encoded + "'))");
        sb.AppendLine("[void][System.Diagnostics.Process]::Start($matrixStartInfo)");
        return sb.ToString();
    }
}
