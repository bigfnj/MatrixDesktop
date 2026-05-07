using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace MatrixDesktopConfigurator;

internal sealed class ConfiguratorState
{
    public int SchemaVersion { get; set; } = 1;
    public int PresetSeedVersion { get; set; } = 0;
    public string? SelectedPresetId { get; set; }
    public JsonObject LastDraft { get; set; } = [];
    public List<UserPreset> UserPresets { get; set; } = [];
}

internal sealed class UserPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled preset";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public JsonObject Values { get; set; } = [];
}
