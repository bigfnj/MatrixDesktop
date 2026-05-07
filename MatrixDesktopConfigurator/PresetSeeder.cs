using System;
using System.Linq;

namespace MatrixDesktopConfigurator;

internal static class PresetSeeder
{
    private const int CurrentSeedVersion = 2;

    private static readonly StarterPreset[] StarterPresets =
    [
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
        if (state.PresetSeedVersion >= CurrentSeedVersion)
        {
            return false;
        }

        var isFirstSeed = state.PresetSeedVersion == 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var preset in StarterPresets)
        {
            var imported = importer.Import(preset.Command, commandBuilder.CreateDefaultDraft());
            var existing = state.UserPresets.FirstOrDefault(existing => string.Equals(existing.Id, preset.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Name = preset.Name;
                existing.Values = imported.Draft;
                existing.UpdatedUtc = now;
                continue;
            }

            if (!isFirstSeed)
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

    private sealed record StarterPreset(string Id, string Name, string Command);
}
