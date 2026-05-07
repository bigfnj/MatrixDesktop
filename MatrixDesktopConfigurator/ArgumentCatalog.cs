using System.Collections.Generic;

namespace MatrixDesktopConfigurator;

internal static class ArgumentCatalog
{
    public static ConfiguratorMetadata Create() => new()
    {
        Groups =
        [
            new ArgumentGroup
            {
                Id = "launch",
                Title = "Launch",
                Fields =
                [
                    Select("windowMode", "Window mode", "app", "borderless",
                        "Borderless all monitors already spans every display. Single monitor targets one display.",
                        ("borderless", "Borderless all monitors"),
                        ("windowed", "Windowed"),
                        ("single-monitor", "Single monitor")),
                    Number("monitor", "Monitor index", "app", null, 0, 16, 1,
                        "0-based target display: 0 is usually primary, 1 is second, 2 is third. Only used with Single monitor."),
                    Toggle("workingArea", "Use working area", "app", false, "--working-area", help: "Taskbar-safe bounds for borderless modes. Ignored for Windowed."),
                    Toggle("topmost", "Always on top", "app", false, "--topmost", "--no-topmost"),
                    Toggle("hideCursor", "Hide cursor", "app", false, "--hide-cursor", "--show-cursor"),
                    Toggle("exitOnAnyKey", "Exit on any key", "app", true, "--exit-on-any-key", "--no-exit-on-any-key"),
                    Toggle("exitOnEsc", "Exit on ESC", "app", true, "--exit-on-esc", "--no-esc-exit"),
                    Toggle("globalKeyExit", "Global key exit", "app", false, "--global-key-exit", "--foreground-key-exit"),
                    Toggle("disableDevTools", "Disable DevTools", "app", false, "--no-devtools", "--devtools"),
                ],
            },
            new ArgumentGroup
            {
                Id = "theme",
                Title = "Theme",
                Fields =
                [
                    Select("version", "Version", "web", "classic",
                        ("classic", "Classic"),
                        ("3d", "3D"),
                        ("megacity", "Mega City"),
                        ("neomatrixology", "Neomatrixology"),
                        ("operator", "Operator"),
                        ("nightmare", "Nightmare"),
                        ("paradise", "Paradise"),
                        ("resurrections", "Resurrections"),
                        ("trinity", "Trinity"),
                        ("morpheus", "Morpheus"),
                        ("bugs", "Bugs"),
                        ("palimpsest", "Palimpsest"),
                        ("twilight", "Twilight"),
                        ("holoplay", "Holoplay")),
                    Select("font", "Font", "web", "matrixcode",
                        ("matrixcode", "Matrix code"),
                        ("resurrections", "Resurrections"),
                        ("gothic", "Gothic"),
                        ("coptic", "Coptic"),
                        ("megacity", "Mega City"),
                        ("huberfishA", "Huberfish A"),
                        ("huberfishD", "Huberfish D"),
                        ("gtarg_tenretniolleh", "GTArg Tenretniolleh"),
                        ("gtarg_alientext", "GTArg Alien Text"),
                        ("neomatrixology", "Neomatrixology")),
                    Select("renderer", "Renderer", "web", "regl",
                        ("regl", "WebGL / REGL"),
                        ("webgpu", "WebGPU")),
                ],
            },
            new ArgumentGroup
            {
                Id = "effect",
                Title = "Effects",
                Fields =
                [
                    Select("effect", "Effect", "web", "palette",
                        ("palette", "Palette"),
                        ("plain", "Plain"),
                        ("none", "Debug / none"),
                        ("stripes", "Stripes"),
                        ("customStripes", "Custom stripes"),
                        ("pride", "Pride"),
                        ("trans", "Trans"),
                        ("transPride", "Trans pride"),
                        ("image", "Image"),
                        ("mirror", "Mirror")),
                    Text("url", "Image URL", "web", ""),
                    Toggle("camera", "Camera", "web", false, "camera"),
                    Toggle("clickRipples", "Click ripples", "web", false, "clickRipples"),
                    Select("clickRippleShape", "Click ripple shape", "web", "circle",
                        ("circle", "Circle"),
                        ("box", "Box"),
                        ("triangle", "Triangle"),
                        ("star", "Star")),
                ],
            },
            new ArgumentGroup
            {
                Id = "colors",
                Title = "Colors",
                Fields =
                [
                    Color("backgroundColor", "Background", "web", 0, 0, 0),
                    Color("cursorColor", "Cursor", "web", 0.47, 1, 0.38),
                    Color("glintColor", "Glint", "web", 1, 1, 1),
                    Palette("palette", "Palette gradient", "web",
                        new ColorStop(0.00, 0.00, 0.00, 0.0),
                        new ColorStop(0.00, 0.25, 0.00, 0.2),
                        new ColorStop(0.35, 0.95, 0.30, 0.7),
                        new ColorStop(0.65, 1.00, 0.60, 1.0)),
                    Stripes("stripeColors", "Stripe colors", "web",
                        "Only used by stripe-based effects. Each row becomes one RGB triplet in the generated stripeColors value.",
                        new ColorValue(0.50, 0.00, 0.50),
                        new ColorValue(0.00, 0.00, 1.00),
                        new ColorValue(0.00, 1.00, 0.00),
                        new ColorValue(1.00, 0.00, 0.00)),
                    Number("cursorIntensity", "Cursor intensity", "web", 2, 0, 8, 0.1),
                    Number("glyphIntensity", "Glyph intensity", "web", 1, 0, 8, 0.1),
                ],
            },
            new ArgumentGroup
            {
                Id = "motion",
                Title = "Motion",
                Fields =
                [
                    Number("animationSpeed", "Animation speed", "web", 1, -4, 4, 0.01),
                    Number("fallSpeed", "Fall speed", "web", 0.3, -4, 4, 0.01),
                    Number("cycleSpeed", "Cycle speed", "web", 0.03, -1, 1, 0.001),
                    Number("forwardSpeed", "Forward speed", "web", 0.25, -4, 4, 0.01),
                    Number("raindropLength", "Raindrop length", "web", 0.75, 0.01, 4, 0.01),
                    Number("slant", "Slant degrees", "web", 0, -180, 180, 1),
                    Toggle("volumetric", "Volumetric / 3D", "web", false, "volumetric"),
                    Toggle("isometric", "Isometric", "web", false, "isometric"),
                ],
            },
            new ArgumentGroup
            {
                Id = "layout",
                Title = "Layout",
                Fields =
                [
                    Number("numColumns", "Columns", "web", 80, 1, 256, 1),
                    Number("density", "Density", "web", 1, 0.01, 4, 0.01),
                    Number("resolution", "Resolution", "web", 0.75, 0.05, 2, 0.01),
                    Number("fps", "FPS", "web", 60, 0, 60, 1),
                    Number("bloomSize", "Bloom size", "web", 0.4, 0, 1, 0.01),
                    Number("bloomStrength", "Bloom strength", "web", 0.7, 0, 1, 0.01),
                    Number("ditherMagnitude", "Dither", "web", 0.05, 0, 1, 0.01),
                ],
            },
            new ArgumentGroup
            {
                Id = "advanced",
                Title = "Advanced",
                Fields =
                [
                    Toggle("glyphFlip", "Flip glyphs", "web", false, "glyphFlip"),
                    Number("glyphRotation", "Glyph rotation", "web", 0, 0, 360, 90),
                    Toggle("loops", "Loop mode", "web", false, "loops"),
                    Toggle("once", "Single frame", "web", false, "once"),
                    Toggle("skipIntro", "Skip intro", "web", true, "skipIntro"),
                    Toggle("suppressWarnings", "Suppress warnings", "web", false, "suppressWarnings"),
                    Select("testFix", "Compatibility fix", "web", "",
                        ("", "None"),
                        ("fwidth_10_1_2022_A", "fwidth 10-1-2022 A"),
                        ("fwidth_10_1_2022_B", "fwidth 10-1-2022 B")),
                ],
            },
        ],
    };

    private static ArgumentDefinition Toggle(string id, string label, string scope, bool defaultValue, string trueFlag, string? falseFlag = null, string? help = null) => new()
    {
        Id = id,
        ArgName = id,
        Label = label,
        Kind = "bool",
        Scope = scope,
        DefaultValue = defaultValue,
        TrueFlag = trueFlag,
        FalseFlag = falseFlag,
        Help = help,
    };

    private static ArgumentDefinition Select(string id, string label, string scope, string defaultValue, params (string Value, string Label)[] options)
        => Select(id, label, scope, defaultValue, null, options);

    private static ArgumentDefinition Select(string id, string label, string scope, string defaultValue, string? help, params (string Value, string Label)[] options) => new()
    {
        Id = id,
        ArgName = id,
        Label = label,
        Kind = "select",
        Scope = scope,
        DefaultValue = defaultValue,
        Help = help,
        Options = ToOptions(options),
    };

    private static ArgumentDefinition Number(string id, string label, string scope, double? defaultValue, double min, double max, double step, string? help = null) => new()
    {
        Id = id,
        ArgName = id,
        Label = label,
        Kind = "number",
        Scope = scope,
        DefaultValue = defaultValue,
        Min = min,
        Max = max,
        Step = step,
        Help = help,
    };

    private static ArgumentDefinition Text(string id, string label, string scope, string defaultValue) => new()
    {
        Id = id,
        ArgName = id,
        Label = label,
        Kind = "text",
        Scope = scope,
        DefaultValue = defaultValue,
    };

    private static ArgumentDefinition Color(string id, string label, string scope, double r, double g, double b) => new()
    {
        Id = id,
        ArgName = id,
        Label = label,
        Kind = "color",
        Scope = scope,
        DefaultValue = new ColorValue(r, g, b),
    };

    private static ArgumentDefinition Palette(string id, string label, string scope, params ColorStop[] stops) => new()
    {
        Id = id,
        ArgName = id,
        Label = label,
        Kind = "palette",
        Scope = scope,
        DefaultValue = stops,
    };

    private static ArgumentDefinition Stripes(string id, string label, string scope, string help, params ColorValue[] colors) => new()
    {
        Id = id,
        ArgName = id,
        Label = label,
        Kind = "stripes",
        Scope = scope,
        Help = help,
        DefaultValue = colors,
    };

    private static List<ArgumentOption> ToOptions((string Value, string Label)[] options)
    {
        var result = new List<ArgumentOption>(options.Length);
        foreach (var option in options)
        {
            result.Add(new ArgumentOption { Value = option.Value, Label = option.Label });
        }

        return result;
    }
}

internal sealed class ConfiguratorMetadata
{
    public List<ArgumentGroup> Groups { get; init; } = [];
}

internal sealed class ArgumentGroup
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public List<ArgumentDefinition> Fields { get; init; } = [];
}

internal sealed class ArgumentDefinition
{
    public string Id { get; init; } = string.Empty;
    public string ArgName { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string? Help { get; init; }
    public object? DefaultValue { get; init; }
    public string? TrueFlag { get; init; }
    public string? FalseFlag { get; init; }
    public List<ArgumentOption> Options { get; init; } = [];
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
}

internal sealed class ArgumentOption
{
    public string Value { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

internal sealed record ColorValue(double R, double G, double B);

internal sealed record ColorStop(double R, double G, double B, double At);
