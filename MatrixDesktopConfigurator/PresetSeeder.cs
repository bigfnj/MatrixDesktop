using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace MatrixDesktopConfigurator;

internal static class PresetSeeder
{
    private const int CurrentSeedVersion = 5;

    private static readonly StarterPreset[] StarterPresets =
    [
        new("seed-uc3d-trilogy", "UC3D Trilogy", "--scene uc3d", 3),
        new(
            "seed-rainbow-haze",
            "rainbow-haze",
            "--hide-cursor font=resurrections fps=30 animationSpeed=0.5 forwardSpeed=0.05 numColumns=220 density=2 effect=stripes renderer=webgpu stripeColors=0.5,0,0.5,0,0,1,0,1,0,0,1,0,0,0,1,0.5,0,0.5 raindropLength=0.5 version=3d"),
        new(
            "seed-paradise",
            "paradise",
            "--hidecursor font=resurrections fps=60 animationSpeed=0.5 forwardSpeed=0.05 numColumns=150 density=2 dropLength=0.25 effect=stripes renderer=webgpu version=paradise"),
        new(
            "seed-stripe-effects",
            "stripe effects",
            "--hidecursor font=resurrections fps=30 animationSpeed=0.5 forwardSpeed=0.05 numColumns=220 density=2 dropLength=0.25 effect=stripes renderer=webgpu effect=stripes stripeColors=1,0,0,1,0.5,0,1,1,0,0,1,0,0,0,1,0.5,0,0.5 raindropLength=0.5 version=3d"),
    ];

    public static bool Seed(ConfiguratorState state, CommandBuilder commandBuilder, ArgumentImporter importer)
    {
        var isFirstSeed = state.PresetSeedVersion == 0;
        var now = DateTimeOffset.UtcNow;
        // Only the exact, untouched starters from versions 3 and 4 belong to this migration.
        // Renaming, editing, or deleting it is a user choice that must survive.
        var previousUc3d = state.UserPresets.FirstOrDefault(preset => preset.Id == "seed-uc3d-trilogy");
        if (previousUc3d?.Name == "UC3D Trilogy"
            && ((state.PresetSeedVersion == 3 && JsonNode.DeepEquals(previousUc3d.Values, PreviousUc3dTemplate(commandBuilder)))
                || (state.PresetSeedVersion == 4 && JsonNode.DeepEquals(previousUc3d.Values, PreviousUc3dProfile(commandBuilder)))))
        {
            if (state.SelectedPresetId == previousUc3d.Id && JsonNode.DeepEquals(state.LastDraft, previousUc3d.Values))
                state.LastDraft = commandBuilder.CreateSceneDefaultDraft("uc3d");
            previousUc3d.Values = commandBuilder.CreateSceneDefaultDraft("uc3d");
            previousUc3d.UpdatedUtc = now;
        }
        // Mark old drafts before the UI merges new defaults. Generic values and names
        // remain intact; only the exact untouched starter opts into original controls.
        var normalized = Uc3dControlProfile.NormalizeSavedDraft(state.LastDraft);
        foreach (var preset in state.UserPresets)
            normalized |= Uc3dControlProfile.NormalizeSavedDraft(preset.Values);
        if (state.PresetSeedVersion >= CurrentSeedVersion) return normalized;
        foreach (var preset in StarterPresets)
        {
            var imported = importer.Import(preset.Command, commandBuilder.CreateDefaultDraft());
            var existing = state.UserPresets.FirstOrDefault(existing => string.Equals(existing.Id, preset.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // MD-11: leave it alone. Once a starter preset exists it belongs to the
                // user, and a seed-version bump used to overwrite its Values, silently
                // discarding whatever they had tuned. Deletion was already respected by the
                // isFirstSeed check below; editing was not, which is the more common case.
                continue;
            }

            if (!isFirstSeed && preset.IntroducedVersion <= state.PresetSeedVersion)
            {
                continue;
            }

            state.UserPresets.Add(new UserPreset
            {
                Id = preset.Id,
                Name = preset.Name,
                CreatedUtc = now,
                UpdatedUtc = now,
                Values = imported.Draft,
            });
        }

        state.PresetSeedVersion = CurrentSeedVersion;
        return true;
    }

    private static JsonObject PreviousUc3dTemplate(CommandBuilder commandBuilder)
    {
        var draft = commandBuilder.CreateDefaultDraft();
        RemoveOriginalControls(draft);
        draft["scene"] = "uc3d";
        draft["volumetric"] = true;
        draft["randomForwardMovement"] = true;
        draft["codeColor"] = new JsonObject { ["r"] = 0.0, ["g"] = 0.698, ["b"] = 0.0 };
        draft["cursorColor"] = new JsonObject { ["r"] = 0.0, ["g"] = 1.0, ["b"] = 0.698 };
        draft["pictureDelay"] = 60.0;
        draft["pictureDuration"] = 30.0;
        return draft;
    }

    private static JsonObject PreviousUc3dProfile(CommandBuilder commandBuilder)
    {
        var draft = commandBuilder.CreateSceneDefaultDraft("uc3d");
        RemoveOriginalControls(draft);
        draft["bloomStrength"] = 0.7;
        return draft;
    }

    private static void RemoveOriginalControls(JsonObject draft)
    {
        draft.Remove("uc3dControls");
        foreach (var key in Uc3dControlProfile.NativeKeys) draft.Remove(key);
    }

    private sealed record StarterPreset(string Id, string Name, string Command, int IntroducedVersion = 1);
}
