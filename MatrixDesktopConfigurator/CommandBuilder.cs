using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatrixDesktopConfigurator;

internal sealed class CommandBuilder
{
    private readonly ConfiguratorMetadata _metadata;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public CommandBuilder(ConfiguratorMetadata metadata)
    {
        _metadata = metadata;
    }

    public JsonObject CreateDefaultDraft()
    {
        var draft = new JsonObject();
        foreach (var field in Fields)
        {
            draft[field.Id] = JsonSerializer.SerializeToNode(field.DefaultValue, _jsonOptions);
        }

        return draft;
    }

    public string BuildCommand(JsonObject values, bool includeDefaults, bool forTest)
    {
        return string.Join(" ", BuildTokens(values, includeDefaults, forTest).Select(QuoteToken));
    }

    public IReadOnlyList<string> BuildArguments(JsonObject values, bool includeDefaults, bool forTest)
    {
        return BuildTokens(values, includeDefaults, forTest).Skip(1).ToArray();
    }

    private List<string> BuildTokens(JsonObject values, bool includeDefaults, bool forTest)
    {
        var tokens = new List<string> { "MatrixDesktop.exe" };
        AddAppTokens(tokens, values, includeDefaults, forTest);
        AddWebTokens(tokens, values, includeDefaults);
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
        else if (windowMode == "single-monitor")
        {
            tokens.Add("--single-monitor");
        }
        else if (forTest)
        {
            tokens.Add("--windowed");
        }
        else if (includeDefaults)
        {
            tokens.Add("--borderless");
        }

        foreach (var field in Fields.Where(static f => f.Scope == "app" && f.Kind == "bool"))
        {
            if (field.Id == "workingArea" && (forTest || windowMode == "windowed"))
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
    }

    private void AddWebTokens(List<string> tokens, JsonObject values, bool includeDefaults)
    {
        var effect = GetString(values, "effect", "palette");
        foreach (var field in Fields.Where(static f => f.Scope == "web"))
        {
            if (field.Id == "stripeColors" && !ArgumentImporter.IsStripeEffect(effect))
            {
                continue;
            }

            var value = values[field.Id];
            if (value is null)
            {
                continue;
            }

            if (!includeDefaults && IsDefault(field, value))
            {
                continue;
            }

            var serialized = SerializeWebValue(field, value);
            if (string.IsNullOrWhiteSpace(serialized))
            {
                continue;
            }

            tokens.Add($"--{field.ArgName}");
            tokens.Add(serialized);
        }
    }

    private string SerializeWebValue(ArgumentDefinition field, JsonNode value)
    {
        return field.Kind switch
        {
            "bool" => GetBoolValue(value) ? "true" : "false",
            "number" => GetNumberValue(value)?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
            "color" => SerializeColor(value),
            "palette" => SerializePalette(value),
            "stripes" => SerializeStripes(value),
            _ => value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString(_jsonOptions),
        };
    }

    private bool IsDefault(ArgumentDefinition field, JsonNode value)
    {
        var defaultNode = JsonSerializer.SerializeToNode(field.DefaultValue, _jsonOptions);
        if (defaultNode is null)
        {
            return IsEmpty(value);
        }

        return NormalizeJson(value) == NormalizeJson(defaultNode);
    }

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

    private static string NormalizeJson(JsonNode node)
    {
        return node.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
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

        if (!token.Any(char.IsWhiteSpace) && !token.Contains('"'))
        {
            return token;
        }

        return "\"" + token.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private IEnumerable<ArgumentDefinition> Fields => _metadata.Groups.SelectMany(static g => g.Fields);
}

internal static class JsonNodeExtensions
{
    public static JsonValueKind GetValueKind(this JsonNode node)
    {
        return node switch
        {
            JsonObject => JsonValueKind.Object,
            JsonArray => JsonValueKind.Array,
            JsonValue value when value.TryGetValue<string>(out _) => JsonValueKind.String,
            JsonValue value when value.TryGetValue<bool>(out _) => JsonValueKind.True,
            JsonValue value when value.TryGetValue<double>(out _) => JsonValueKind.Number,
            _ => JsonValueKind.Undefined,
        };
    }
}
