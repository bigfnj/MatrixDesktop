using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace MatrixDesktopConfigurator;

// Records with mutable get/set properties — auto-generated equality/hash/ToString
// help with debugging and reduce boilerplate vs the equivalent sealed classes.
// We keep mutable setters because both types are populated by System.Text.Json
// deserialisation and updated in-place as the user edits presets.
internal sealed record ConfiguratorState
{
    public int SchemaVersion { get; set; } = 1;
    public int PresetSeedVersion { get; set; } = 0;
    public string? SelectedPresetId { get; set; }
    public JsonObject LastDraft { get; set; } = [];
    public List<UserPreset> UserPresets { get; set; } = [];
}

internal sealed record UserPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled preset";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public JsonObject Values { get; set; } = [];
}
