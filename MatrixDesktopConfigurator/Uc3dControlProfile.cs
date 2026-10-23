using System.Text.Json.Nodes;

namespace MatrixDesktopConfigurator;

// Original-unit controls are separate from the earlier renderer's generic units.
// Keeping both sets in saved drafts allows switching back without discarding edits.
internal static class Uc3dControlProfile
{
    internal static readonly HashSet<string> NativeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "uc3dAmount", "uc3dGlyphSize", "uc3dFallSpeed", "uc3dForwardSpeed",
        "uc3dPictureScale", "uc3dPictureDelay", "uc3dClockSize",
    };

    internal static readonly HashSet<string> LegacyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "numColumns", "density", "fallSpeed", "forwardSpeed", "raindropLength",
        "pictureScale", "pictureDelay", "clockSize", "cycleSpeed", "isometric",
    };

    internal static readonly HashSet<string> OriginalInactiveKeys = new(LegacyKeys, StringComparer.OrdinalIgnoreCase);

    internal static bool IsUc3d(JsonObject draft) => draft["scene"] is JsonValue value && value.TryGetValue<string>(out var scene) && scene == "uc3d";

    private static string? ReadMode(JsonObject draft) => draft["uc3dControls"] is JsonValue value && value.TryGetValue<string>(out var mode) ? mode : null;

    internal static string Infer(IEnumerable<string> explicitKeys, JsonObject draft)
    {
        var keys = explicitKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Contains("uc3dControls") && ReadMode(draft) is "original" or "legacy")
            return ReadMode(draft)!;
        if (keys.Overlaps(NativeKeys)) return "original";
        return keys.Overlaps(LegacyKeys) ? "legacy" : "original";
    }

    internal static string Mode(JsonObject draft)
    {
        if (ReadMode(draft) is "original" or "legacy") return ReadMode(draft)!;
        // A full saved draft predating these controls has only generic keys.
        return NativeKeys.Any(draft.ContainsKey) ? "original" : "legacy";
    }

    internal static bool NormalizeSavedDraft(JsonObject draft)
    {
        if (!IsUc3d(draft) || ReadMode(draft) is "original" or "legacy") return false;
        draft["uc3dControls"] = Mode(draft);
        return true;
    }

    internal static bool IsActive(string key, JsonObject draft)
    {
        if (!IsUc3d(draft)) return key != "uc3dControls" && !NativeKeys.Contains(key);
        return Mode(draft) == "original" ? !OriginalInactiveKeys.Contains(key) : !NativeKeys.Contains(key);
    }
}
