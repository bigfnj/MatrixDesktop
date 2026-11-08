using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatrixDesktopConfigurator;

// State changes behind the configurator's preset requests, and the payload readers they use.
// Free of WinForms so the core harness runs the same code as ConfiguratorForm.
internal static class PresetOperations
{
    // AUD-02: a rename changes the name and the modification time, nothing else. The page used
    // to rename through savePreset with the working draft, which replaced the saved values.
    public static UserPreset Rename(ConfiguratorState state, JsonElement payload, DateTimeOffset now)
    {
        var id = ReadOptionalString(payload, "id");
        var preset = state.UserPresets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The preset to rename no longer exists.");
        var name = ReadOptionalString(payload, "name")?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            preset.Name = name;
            preset.UpdatedUtc = now;
        }

        return preset;
    }

    public static JsonObject ReadObject(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(propertyName, out var element))
        {
            return JsonNode.Parse(element.GetRawText())?.AsObject() ?? [];
        }

        return [];
    }

    public static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(propertyName, out var element))
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        }

        return null;
    }
}
