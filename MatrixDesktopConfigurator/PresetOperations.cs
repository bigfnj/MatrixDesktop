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

    // AUD-17: LastDraft follows every edit, so choosing a preset used to leave no copy of the
    // unsaved work. The page sends scratchDraft; without one, an unlinked draft is the scratch
    // draft and a linked one keeps the stored copy (an older page or close snapshot).
    public static void SaveDraft(ConfiguratorState state, JsonElement payload)
    {
        state.LastDraft = ReadObject(payload, "draft");
        state.SelectedPresetId = ReadOptionalString(payload, "selectedPresetId");
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("scratchDraft", out var scratch)
            && scratch.ValueKind == JsonValueKind.Object)
        {
            state.ScratchDraft = ReadObject(payload, "scratchDraft");
        }
        else if (string.IsNullOrWhiteSpace(state.SelectedPresetId))
        {
            state.ScratchDraft = StorageService.CloneObject(state.LastDraft);
        }
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
