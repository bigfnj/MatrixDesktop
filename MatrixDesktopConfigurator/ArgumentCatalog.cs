using System.Collections.Generic;

namespace MatrixDesktopConfigurator;

internal static class ArgumentCatalog
{
    public static ConfiguratorMetadata Create() => new()
    {
        SceneDefaults = new()
        {
            ["uc3d"] = new()
            {
                ["uc3dControls"] = "original",
                ["uc3dAmount"] = 500.0,
                ["uc3dGlyphSize"] = 12.0,
                ["uc3dFallSpeed"] = 6.0,
                ["uc3dForwardSpeed"] = 6.0,
                ["uc3dPictureScale"] = 100.0,
                ["uc3dPictureDelay"] = 60.0,
                ["uc3dClockSize"] = 90.0,
                ["bloomStrength"] = 0.0,
                ["volumetric"] = true,
                ["numColumns"] = 100.0,
                ["density"] = 5.0,
                ["fallSpeed"] = 0.045,
                ["forwardSpeed"] = -0.675,
                ["codeColor"] = new ColorValue(128.0 / 255.0, 0, 1),
                ["cursorColor"] = new ColorValue(1, 128.0 / 255.0, 1),
                ["sidewaysMovement"] = true,
                ["randomForwardMovement"] = true,
                ["pictureMonochrome"] = false,
                ["pictureDelay"] = 60.0,
                ["pictureDuration"] = 30.0,
            },
        },
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
                    Select("scene", "Scene", "web", "matrix", ("matrix", "Matrix"), ("uc3d", "UC3D")),
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
                    // These are the RGB conversions of config.js's HSL defaults, NOT
                    // approximations. Both of these had been eyeballed and were visibly off:
                    // cursorColor was hsl(0.242, 1, 0.73) = (0.756, 1.000, 0.460) but read
                    // (0.47, 1, 0.38), and every palette stop was wrong, with the last one
                    // also sitting at 1.0 instead of 0.8, which changes the gradient's shape.
                    // Cost of the drift: the swatch showed a colour the app never renders, and
                    // because CommandBuilder only emits a field that differs from its default,
                    // nudging any one stop baked the whole wrong palette into the command.
                    // tests/run-gate.ps1 now recomputes these from config.js on every run.
                    Color("cursorColor", "Cursor", "web", 0.756, 1.000, 0.460),
                    Color("glintColor", "Glint", "web", 1, 1, 1),
                    Palette("palette", "Palette gradient", "web",
                        new ColorStop(0.000, 0.000, 0.000, 0.0),
                        new ColorStop(0.092, 0.380, 0.020, 0.2),
                        new ColorStop(0.538, 0.970, 0.430, 0.7),
                        new ColorStop(0.692, 0.980, 0.620, 0.8)),
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
                    Number("fallSpeed", "Fall speed", "web", 0.3, -4, 4, 0.001),
                    Number("cycleSpeed", "Cycle speed", "web", 0.03, -1, 1, 0.001),
                    Number("forwardSpeed", "Forward speed", "web", 0.25, -4, 4, 0.001),
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
                    Number("density", "Density", "web", 1, 0.01, 8, 0.01),
                    Number("resolution", "Resolution", "web", 0.75, 0.05, 2, 0.01),
                    Number("fps", "FPS", "web", 60, 0, 60, 1),
                    Number("bloomSize", "Bloom size", "web", 0.4, 0, 1, 0.01),
                    Number("bloomStrength", "Bloom strength", "web", 0.7, 0, 1, 0.01),
                    Number("ditherMagnitude", "Dither", "web", 0.05, 0, 1, 0.01),
                ],
            },
            new ArgumentGroup
            {
                Id = "uc3d",
                Title = "UC3D",
                Fields =
                [
                    Select("uc3dControls", "Control units", "web", "original",
                        "Original controls use the screensaver's slider values. Legacy controls preserve existing MatrixDesktop presets.",
                        ("original", "Original screensaver controls"), ("legacy", "Legacy MatrixDesktop controls")),
                    Slider("uc3dAmount", "Code amount: Less / More", 500, 100, 1500, "Number of falling code streams. Independent of code size."),
                    Slider("uc3dGlyphSize", "Code size: Small / Large", 12, 10, 40, "Original code-size slider units."),
                    Slider("uc3dFallSpeed", "Fall speed: Slow / Fast", 6, 0, 150, "Original fall-speed slider. Zero retains the original minimum falling motion."),
                    Slider("uc3dForwardSpeed", "Travel: Backward / Forward", 6, 0, 120, "60 is stationary. Used for constant travel when random movement is off."),
                    Slider("uc3dPictureScale", "Picture size: Large / Small", 100, 100, 300, "Original slider units. Higher values form a smaller picture."),
                    Slider("uc3dPictureDelay", "Picture delay (seconds)", 60, 0, 120, "Original wait between picture sequences."),
                    Slider("uc3dClockSize", "Clock size: Small / Large", 90, 50, 170, "Original clock-size slider units; 90 is the installed default."),
                    Color("codeColor", "Normal code color", "web", 0, 0.698, 0),
                    Toggle("donorGlyphs", "Use local donor glyphs", "web", false, "donorGlyphs", help: "Use the original character atlas from your imported donor media folder."),
                    Toggle("shadedLines", "Shaded 2D lines", "web", true, "shadedLines"),
                    Toggle("sidewaysMovement", "Sideways movement", "web", false, "sidewaysMovement"),
                    Toggle("mouseRotation", "Mouse-controlled rotation", "web", false, "mouseRotation", help: "Move the mouse to rotate; right-click stops, left-click resets, wheel rolls. Resets after 20 seconds idle."),
                    Toggle("codeRotation", "Automatic 3D rotation", "web", true, "codeRotation"),
                    Toggle("forwardMovement", "Forward/back movement", "web", true, "forwardMovement"),
                    Toggle("randomForwardMovement", "Random forward/back movement", "web", false, "randomForwardMovement"),
                    Toggle("showPictures", "Form pictures from code", "web", false, "showPictures"),
                    Toggle("pictureMonochrome", "Monochrome pictures", "web", true, "pictureMonochrome"),
                    Toggle("pictureCaptions", "Picture captions", "web", true, "pictureCaptions"),
                    Toggle("showClock", "Show clock", "web", false, "showClock"),
                    Toggle("clock12Hour", "12-hour clock", "web", false, "clock12Hour"),
                    Toggle("desktopBackground", "Desktop snapshot background", "web", false, "desktopBackground", help: "Capture the desktop once when the app starts. This is a snapshot, not live wallpaper."),
                    Number("pictureDepth", "Picture relief depth", "web", 0, 0, 2, 0.05, "3D luminance relief. Set to zero for a flat picture."),
                    Number("pictureScale", "Picture scale", "web", 1, 0.1, 3, 0.05, "Aspect-preserving picture size."),
                    Number("pictureDelay", "Picture delay (seconds)", "web", 20, 1, 180, 1, "Wait between picture sequences."),
                    Number("pictureDuration", "Picture duration (seconds)", "web", 8, 1, 60, 1, "How long a picture sequence lasts."),
                    Number("clockSize", "Clock size", "web", 1, 0.25, 4, 0.1, "Scale of the centered clock."),
                    Number("soundVolume", "Sound volume", "web", 0.25, 0, 1, 0.05, "Preview starts muted. Use its Test sound button or Test Argument to hear sound."),
                    Select("pictureSource", "Picture source", "web", "all", ("all", "All available pictures"), ("matrix", "The Matrix"), ("reloaded", "Reloaded"), ("revolutions", "Revolutions"), ("custom", "My pictures")),
                    Text("pictureDirectory", "My picture folder", "web", ""),
                    Text("donorDirectory", "Local donor media folder", "web", ""),
                    Select("sound", "Sound", "web", "off", ("off", "None"), ("effects", "Code sizzle"), ("music", "Ambient music / local file")),
                    Text("musicFile", "Local music file (optional)", "web", ""),
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

    private static ArgumentDefinition Slider(string id, string label, double defaultValue, double min, double max, string help) => new()
    {
        Id = id,
        ArgName = id,
        Label = label,
        Kind = "number",
        Scope = "web",
        DefaultValue = defaultValue,
        Min = min,
        Max = max,
        Step = 1,
        Slider = true,
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
    public Dictionary<string, Dictionary<string, object?>> SceneDefaults { get; init; } = [];
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
    public bool Slider { get; init; }
}

internal sealed class ArgumentOption
{
    public string Value { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

internal sealed record ColorValue(double R, double G, double B);

internal sealed record ColorStop(double R, double G, double B, double At);
