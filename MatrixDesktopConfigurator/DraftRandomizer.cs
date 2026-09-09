using System;
using System.Text.Json.Nodes;

namespace MatrixDesktopConfigurator;

internal sealed class DraftRandomizer
{
    private static readonly string[] SafeVersions =
    [
        "classic",
        "3d",
        "megacity",
        "neomatrixology",
        "operator",
        "nightmare",
        "paradise",
        "resurrections",
        "trinity",
        "morpheus",
        "bugs",
        "palimpsest",
        "twilight",
    ];

    private static readonly string[] SafeFonts =
    [
        "matrixcode",
        "resurrections",
        "gothic",
        "coptic",
        "megacity",
        "huberfishA",
        "huberfishD",
        "neomatrixology",
    ];

    private static readonly string[] SafeEffects =
    [
        "palette",
        "stripes",
        "customStripes",
        "pride",
        "trans",
        "transPride",
    ];

    private static readonly string[] RippleShapes =
    [
        "circle",
        "box",
        "triangle",
        "star",
    ];

    private static readonly double[] FrameRates = [30, 45, 60];
    private readonly Random _random;

    public DraftRandomizer()
        : this(null)
    {
    }

    // A seed makes the randomizer reproducible, which is what lets the regression harness
    // assert anything about it at all. Production keeps the parameterless constructor.
    public DraftRandomizer(int? seed)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public JsonObject Randomize(JsonObject values, string? scope)
    {
        var draft = StorageService.CloneObject(values);
        switch ((scope ?? "visual").Trim().ToLowerInvariant())
        {
            case "colors":
                RandomizeColors(draft);
                break;

            case "motion":
                RandomizeMotionAndLayout(draft);
                break;

            default:
                RandomizeVisualPreset(draft);
                break;
        }

        return draft;
    }

    private void RandomizeVisualPreset(JsonObject draft)
    {
        var effect = Pick(SafeEffects);
        draft["version"] = Pick(SafeVersions);
        draft["font"] = Pick(SafeFonts);
        draft["renderer"] = Chance(0.7) ? "webgpu" : "regl";
        draft["effect"] = effect;
        draft["camera"] = false;
        draft["url"] = string.Empty;
        draft["once"] = false;
        draft["loops"] = false;
        draft["suppressWarnings"] = false;
        draft["testFix"] = string.Empty;
        draft["clickRipples"] = Chance(0.25);
        draft["clickRippleShape"] = Pick(RippleShapes);

        RandomizeColors(draft);
        RandomizeMotionAndLayout(draft);
    }

    private void RandomizeColors(JsonObject draft)
    {
        var backgroundHue = NextDouble();
        draft["backgroundColor"] = ColorFromHsl(backgroundHue, Range(0.25, 0.75), Range(0.01, 0.07));

        var accentHue = NextDouble();
        draft["cursorColor"] = ColorFromHsl(accentHue, Range(0.7, 1), Range(0.55, 0.75));
        draft["glintColor"] = ColorFromHsl(WrapHue(accentHue + Range(0.08, 0.24)), Range(0.5, 0.95), Range(0.72, 0.92));
        draft["palette"] = BuildPalette(accentHue);

        var effect = GetString(draft, "effect", "palette");
        if (ArgumentImporter.IsStripeEffect(effect))
        {
            draft["stripeColors"] = BuildStripeColors(accentHue);
        }
    }

    private void RandomizeMotionAndLayout(JsonObject draft)
    {
        draft["fps"] = Pick(FrameRates);
        draft["animationSpeed"] = Round(Range(0.3, 1.4));
        draft["fallSpeed"] = Round(Range(0.16, 0.65));
        draft["cycleSpeed"] = Round(Range(0.015, 0.08), 3);
        draft["forwardSpeed"] = Round(Range(0.02, 0.2));
        draft["raindropLength"] = Round(Range(0.25, 1.0));
        draft["slant"] = Pick([-12, -8, -4, 0, 0, 0, 4, 8, 12]);
        draft["volumetric"] = Chance(0.35);
        draft["isometric"] = Chance(0.2);

        draft["numColumns"] = Pick([80, 100, 120, 150, 180, 200, 220]);
        draft["density"] = Round(Range(0.75, 2.5));
        draft["resolution"] = Round(Range(0.55, 1.0));
        draft["bloomSize"] = Round(Range(0.25, 0.85));
        draft["bloomStrength"] = Round(Range(0.35, 0.95));
        draft["ditherMagnitude"] = Round(Range(0.02, 0.12));
        draft["cursorIntensity"] = Round(Range(1.2, 3.8));
        draft["glyphIntensity"] = Round(Range(0.8, 2.4));
    }

    private JsonArray BuildPalette(double baseHue)
    {
        var count = NextInt(3, 7);
        var result = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            var at = count == 1 ? 0 : (double)i / (count - 1);
            var hue = WrapHue(baseHue + (i * Range(0.08, 0.18)) + Range(-0.025, 0.025));
            var color = ColorFromHsl(hue, Range(0.5, 1), Range(0.08 + at * 0.5, 0.18 + at * 0.55));
            color["at"] = Round(at, 3);
            result.Add(color);
        }

        return result;
    }

    private JsonArray BuildStripeColors(double baseHue)
    {
        var count = NextInt(2, 9);
        var result = new JsonArray();
        var spacing = Range(0.11, 0.22);
        for (var i = 0; i < count; i++)
        {
            result.Add(ColorFromHsl(WrapHue(baseHue + i * spacing), Range(0.65, 1), Range(0.38, 0.68)));
        }

        return result;
    }

    // MD-33: delegates to ColorConverter instead of carrying a second copy of the same
    // piecewise function. This class predated that extraction and was not migrated with the
    // importer, so the codebase held two implementations of identical maths in one project.
    // Kept internal and static so the regression harness can still assert the equivalence
    // that made removing the duplicate safe.
    internal static JsonObject ColorFromHsl(double h, double s, double l)
    {
        var rgb = ColorConverter.HslToRgb(h, s, l);
        return Color(rgb[0], rgb[1], rgb[2]);
    }

    private static JsonObject Color(double r, double g, double b) => new()
    {
        ["r"] = Round(Clamp01(r)),
        ["g"] = Round(Clamp01(g)),
        ["b"] = Round(Clamp01(b)),
    };

    private T Pick<T>(T[] values)
    {
        return values[NextInt(0, values.Length)];
    }

    private bool Chance(double probability)
    {
        return NextDouble() < probability;
    }

    private int NextInt(int minInclusive, int maxExclusive)
    {
        return _random.Next(minInclusive, maxExclusive);
    }

    private double NextDouble()
    {
        return _random.NextDouble();
    }

    private double Range(double min, double max)
    {
        return min + (max - min) * NextDouble();
    }

    private static double Round(double value, int digits = 3)
    {
        return Math.Round(value, digits, MidpointRounding.AwayFromZero);
    }

    private static double WrapHue(double hue)
    {
        return ((hue % 1) + 1) % 1;
    }

    private static double Clamp01(double value) => ColorConverter.Clamp01(value);

    private static string GetString(JsonObject obj, string key, string defaultValue)
    {
        return obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : defaultValue;
    }
}
