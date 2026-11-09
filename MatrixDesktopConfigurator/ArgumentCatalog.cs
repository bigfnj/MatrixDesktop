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
        VersionDefaults = CreateVersionDefaults(),
        // The original screensaver's settings dialog groups Normal, Highlight and Background
        // colour. UC3D reads the same shared keys as the Matrix scene (cursorColor is the
        // leading-glyph highlight: web/js/uc3d/runtime.js:44-45, scaled by cursorIntensity at
        // :103), so while UC3D is selected a placement changes only which section shows the
        // editor and what it is called. Keys, commands, imports and presets are untouched.
        // CommandBuilderTests holds every placement to a real field, group and anchor.
        ScenePlacements = new()
        {
            ["uc3d"] =
            [
                new("cursorColor", "Highlight code color", Group: "uc3d", After: "codeColor",
                    Help: "Leading glyph of each stream. The Matrix scene shows this same setting as Cursor (cursorColor)."),
                new("backgroundColor", "Background color", Group: "uc3d", After: "cursorColor",
                    Help: "The Matrix scene shows this same setting as Background (backgroundColor)."),
                new("cursorIntensity", "Highlight intensity",
                    Help: "Brightness of the highlight code color (cursorIntensity)."),
            ],
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
                        ("wallpaper", "Live desktop wallpaper"),
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
                    Select("processPriority", "Process priority", "app", "auto",
                        "Automatic uses idle priority for wallpaper; normal for other window modes.",
                        ("auto", "Automatic"), ("idle", "Idle"), ("below-normal", "Below normal"),
                        ("normal", "Normal"), ("above-normal", "Above normal"), ("high", "High"), ("realtime", "Realtime")),
                    Toggle("wallpaperInput", "Wallpaper mouse interaction", "app", false, "--wallpaper-input", "--no-wallpaper-input"),
                    Toggle("wallpaperPauseFullscreen", "Pause wallpaper for fullscreen apps", "app", true, "--wallpaper-pause-fullscreen", "--no-wallpaper-pause-fullscreen"),
                    Toggle("wallpaperPauseBattery", "Pause wallpaper on battery", "app", false, "--wallpaper-pause-battery", "--no-wallpaper-pause-battery"),
                ],
            },
            new ArgumentGroup
            {
                Id = "theme",
                Title = "Theme",
                Fields =
                [
                    Select("scene", "Scene", "web", "matrix", ("matrix", "Matrix"), ("uc3d", "UC3D"), ("zmatrix", "ZMatrix")),
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
                    // Labels are the original v3.4 settings dialog's own slider names, so the
                    // owner can match each control to the screensaver they are replacing.
                    Slider("uc3dAmount", "Line density: Low / Very high", 500, 100, 1500, "Number of falling code streams, 100 to 1500. Independent of glyph size."),
                    Slider("uc3dGlyphSize", "Glyph size: Small / Big", 12, 10, 40, "Original glyph-size slider units."),
                    Slider("uc3dFallSpeed", "Fall speed: Slow / Fast", 6, 0, 150, "Original fall-speed slider. Zero retains the original minimum falling motion."),
                    Slider("uc3dForwardSpeed", "Forward/back speed: Back / Forward", 6, 0, 120, "60 is stationary. Used for constant travel when random movement is off."),
                    Slider("uc3dPictureScale", "Picture scale: Full size / 1/3 size", 100, 100, 300, "Original slider units. Higher values form a smaller picture."),
                    Slider("uc3dPictureDelay", "Picture delay: Shorter / Longer", 60, 0, 120, "Seconds between picture sequences, in original slider units."),
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
                    Toggle("desktopBackground", "Desktop snapshot background", "web", false, "desktopBackground", help: "Capture the desktop once when the app starts. Wallpaper mode and the screensaver preview use the desktop wallpaper only, never other windows. This is a snapshot, not live wallpaper."),
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
            .. ZMatrixControlProfile.CreateGroups(),
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

    // What each config.js version (web/js/config.js:180-437) draws for a catalog field that a
    // command leaves out, where it differs from classic, in catalog units: RGB colours rounded
    // as the catalog rounds them, slant in degrees. tests/effective-defaults-tests.mjs holds
    // every entry to the real config.js, so a version edit there fails until this follows.
    private static Dictionary<string, Dictionary<string, object?>> CreateVersionDefaults() => new()
    {
        ["3d"] = new() { ["fallSpeed"] = 0.5, ["raindropLength"] = 0.3, ["volumetric"] = true },
        ["megacity"] = new() { ["font"] = "megacity", ["animationSpeed"] = 0.5, ["numColumns"] = 40.0 },
        ["neomatrixology"] = new()
        {
            ["font"] = "neomatrixology", ["cursorColor"] = new ColorValue(0.999, 1, 0.5),
            ["palette"] = new ColorStop[] { new(0, 0, 0, 0), new(0.38, 0.344, 0.02, 0.2), new(0.97, 0.916, 0.43, 0.7), new(0.98, 0.944, 0.62, 0.8) },
            ["animationSpeed"] = 0.8, ["numColumns"] = 40.0,
        },
        ["operator"] = new()
        {
            ["cursorColor"] = new ColorValue(0.32, 1, 0.49),
            ["palette"] = new ColorStop[] { new(0, 0, 0, 0), new(0.1, 0.9, 0.42, 0.5), new(1, 1, 1, 1) },
            ["cursorIntensity"] = 3.0, ["fallSpeed"] = 0.6, ["cycleSpeed"] = 0.01, ["raindropLength"] = 1.5,
            ["numColumns"] = 108.0, ["bloomSize"] = 0.6, ["bloomStrength"] = 0.75,
        },
        ["nightmare"] = new()
        {
            ["font"] = "gothic",
            ["palette"] = new ColorStop[] { new(0, 0, 0, 0), new(0.4, 0, 0, 0.2), new(0.8, 0, 0, 0.4), new(1, 0.76, 0.4, 0.7), new(1, 1, 1, 1) },
            ["fallSpeed"] = 1.2, ["cycleSpeed"] = 0.35, ["raindropLength"] = 0.5, ["slant"] = 22.5, ["numColumns"] = 60.0,
        },
        ["paradise"] = new()
        {
            ["font"] = "coptic",
            ["palette"] = new ColorStop[] { new(0, 0, 0, 0), new(0.54, 0.06, 0.06, 0.3), new(0.9, 0.58, 0.1, 0.5), new(1, 0.68, 0.2, 0.6), new(1, 0.92, 0.8, 0.9) },
            ["fallSpeed"] = 0.02, ["cycleSpeed"] = 0.005, ["raindropLength"] = 0.4, ["numColumns"] = 40.0, ["bloomStrength"] = 1.0,
        },
        ["resurrections"] = new()
        {
            ["font"] = "resurrections", ["cursorColor"] = new ColorValue(0.699, 1, 0.6),
            ["palette"] = new ColorStop[] { new(0, 0, 0, 0), new(0.2, 1, 0.4, 0.92), new(1, 1, 1, 1) },
            ["numColumns"] = 70.0,
        },
        ["trinity"] = new()
        {
            ["font"] = "resurrections", ["cursorColor"] = new ColorValue(0.699, 1, 0.6), ["glintColor"] = new ColorValue(1, 0.829, 0.2),
            ["palette"] = new ColorStop[] { new(0, 0, 0, 0), new(0.2, 0.8, 0.332, 1) },
            ["cycleSpeed"] = 0.01, ["forwardSpeed"] = 0.2, ["raindropLength"] = 0.3, ["volumetric"] = true, ["numColumns"] = 60.0, ["density"] = 0.75,
        },
        ["morpheus"] = new()
        {
            ["font"] = "resurrections", ["cursorColor"] = new ColorValue(0.701, 1, 0.7), ["glintColor"] = new ColorValue(0, 1, 0.4),
            ["palette"] = new ColorStop[] { new(0, 0, 0, 0), new(0.8, 0.2, 0.308, 1) },
            ["cycleSpeed"] = 0.015, ["forwardSpeed"] = 0.1, ["raindropLength"] = 0.4, ["volumetric"] = true, ["numColumns"] = 60.0, ["density"] = 0.75,
        },
        ["bugs"] = new()
        {
            ["font"] = "resurrections", ["cursorColor"] = new ColorValue(0.3, 0.5, 1), ["glintColor"] = new ColorValue(0.2, 0.4, 1),
            ["palette"] = new ColorStop[] { new(0, 0, 0, 0), new(0.8, 0.704, 0.2, 1) },
            ["cycleSpeed"] = 0.01, ["forwardSpeed"] = 0.4, ["raindropLength"] = 0.3, ["volumetric"] = true, ["numColumns"] = 60.0, ["density"] = 0.75,
        },
        ["palimpsest"] = new()
        {
            ["font"] = "huberfishA",
            ["palette"] = new ColorStop[] { new(0.925, 0.92, 0.875, 0), new(0.02, 0.084, 0.18, 0.4) },
            ["fallSpeed"] = 0.5, ["raindropLength"] = 1.2, ["slant"] = -11.25, ["numColumns"] = 40.0, ["bloomStrength"] = 0.2,
        },
        ["twilight"] = new()
        {
            ["font"] = "huberfishD", ["cursorColor"] = new ColorValue(0.999, 1, 0.6),
            ["palette"] = new ColorStop[] { new(0, 0.04, 0.1, 0), new(0.02, 0.084, 0.18, 0.1), new(0.9, 0.1, 0.676, 0.5), new(1, 0.92, 0.2, 0.8) },
            ["cursorIntensity"] = 1.5, ["fallSpeed"] = 0.1, ["raindropLength"] = 0.9, ["numColumns"] = 50.0, ["bloomStrength"] = 0.1,
        },
        ["holoplay"] = new()
        {
            ["font"] = "resurrections", ["cursorColor"] = new ColorValue(0.699, 1, 0.6), ["glintColor"] = new ColorValue(1, 0.829, 0.2),
            ["palette"] = new ColorStop[] { new(0, 0, 0, 0), new(0.2, 0.8, 0.332, 1) },
            ["cycleSpeed"] = 0.01, ["forwardSpeed"] = 0.0, ["raindropLength"] = 0.3, ["volumetric"] = true, ["numColumns"] = 20.0,
            ["density"] = 3.0, ["bloomStrength"] = 0.0, ["ditherMagnitude"] = 0.0,
        },
    };

    // regl/stripePass.js:10-29 (and webgpu/stripePass.js): the rows a stripe effect draws when
    // stripeColors is absent, expanded as the renderer expands them because make1DTexture
    // filters linearly, so three rows of a colour do not draw like one.
    private static ColorValue[] Rows(int repeat, params ColorValue[] colors) => colors.SelectMany(color => Enumerable.Repeat(color, repeat)).ToArray();

    internal static readonly ColorValue[] TransPrideStripes = Rows(3,
        new(0.36, 0.81, 0.98), new(0.96, 0.66, 0.72), new(1, 1, 1), new(0.96, 0.66, 0.72), new(0.36, 0.81, 0.98));

    internal static readonly ColorValue[] PrideStripes = Rows(2,
        new(0.89, 0.01, 0.01), new(1, 0.55, 0), new(1, 0.93, 0), new(0, 0.5, 0.15), new(0, 0.3, 1), new(0.46, 0.03, 0.53));

    // config.js:654-670: naming any effect, palette included, makes the cursor white (except in
    // UC3D), the cursor intensity 2, the glint white and the glyph intensity 1, over the
    // version's own values. CommandBuilder never names the default palette effect, so
    // EffectiveDefault skips palette's entry and ArgumentImporter applies it to a command that
    // names palette (R2-11).
    internal static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> CreateEffectDefaults(IEnumerable<ArgumentGroup> groups)
    {
        var white = new ColorValue(1, 1, 1);
        var effects = groups.SelectMany(group => group.Fields).Single(field => field.Id == "effect").Options
            .Select(option => option.Value).ToArray();
        var result = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>();
        foreach (var scene in new[] { "matrix", "uc3d" })
        {
            var byEffect = new Dictionary<string, Dictionary<string, object?>>();
            foreach (var effect in effects)
            {
                var values = new Dictionary<string, object?> { ["cursorIntensity"] = 2.0, ["glintColor"] = white, ["glyphIntensity"] = 1.0 };
                if (scene != "uc3d") values["cursorColor"] = white;
                if (ArgumentImporter.IsStripeEffect(effect)) values["stripeColors"] = effect == "pride" ? PrideStripes : TransPrideStripes;
                byEffect[effect] = values;
            }
            result[scene] = byEffect;
        }
        return result;
    }
}

internal sealed class ConfiguratorMetadata
{
    public List<ArgumentGroup> Groups { get; init; } = [];
    public Dictionary<string, Dictionary<string, object?>> SceneDefaults { get; init; } = [];
    public Dictionary<string, Dictionary<string, object?>> VersionDefaults { get; init; } = [];
    // config.js:438-442 aliases, which commands may name.
    public Dictionary<string, string> VersionAliases { get; init; } = new()
        { ["throwback"] = "operator", ["updated"] = "resurrections", ["1999"] = "operator", ["2003"] = "classic", ["2021"] = "resurrections" };
    private Dictionary<string, Dictionary<string, Dictionary<string, object?>>>? _effectDefaults;
    // effectDefaults[scene][effect][fieldId]; scene is "matrix" or "uc3d" (ZMatrix has no effect).
    public Dictionary<string, Dictionary<string, Dictionary<string, object?>>> EffectDefaults => _effectDefaults ??= ArgumentCatalog.CreateEffectDefaults(Groups);

    // What the renderer draws for a field the command leaves out, in config.js's order
    // (:679-698): an effect's implied values (never palette's, which the command does not name)
    // over the scene profile over the version over the catalog. Version and effect are not
    // emitted for ZMatrix, so it renders classic there.
    // app.js effectiveDefaultsFor is the page's copy of this rule.
    internal object? EffectiveDefault(ArgumentDefinition field, string scene, string? uc3dMode, string? version, string? effect)
    {
        if (scene == "uc3d" && uc3dMode == "legacy" && field.Id == "bloomStrength") return field.DefaultValue;
        if (scene != "zmatrix" && effect is not null && effect != "palette"
            && EffectDefaults.TryGetValue(scene == "uc3d" ? "uc3d" : "matrix", out var byEffect)
            && byEffect.TryGetValue(effect, out var effectValues) && effectValues.TryGetValue(field.Id, out var effectValue))
            return effectValue;
        if (SceneDefaults.TryGetValue(scene, out var sceneValues) && sceneValues.TryGetValue(field.Id, out var sceneValue)) return sceneValue;
        if (scene != "zmatrix" && version is not null
            && VersionDefaults.TryGetValue(VersionAliases.GetValueOrDefault(version, version), out var versionValues)
            && versionValues.TryGetValue(field.Id, out var versionValue))
            return versionValue;
        return field.DefaultValue;
    }

    // The default CommandBuilder omitted before version and effect were modelled (scene and
    // catalog only). Used solely to carry old drafts across without changing their render.
    internal object? LegacyDefault(ArgumentDefinition field, string scene, string? uc3dMode)
        => scene == "uc3d" && uc3dMode == "legacy" && field.Id == "bloomStrength" ? field.DefaultValue
            : SceneDefaults.TryGetValue(scene, out var sceneValues) && sceneValues.TryGetValue(field.Id, out var sceneValue) ? sceneValue
            : field.DefaultValue;
    // Presentation only: read by configurator/js/app.js sceneSections, never by CommandBuilder,
    // the importers or the randomizer, which all walk Groups.
    public Dictionary<string, List<ScenePlacement>> ScenePlacements { get; init; } = [];
    public List<ArgumentDefinition> FontFields { get; init; } = ZMatrixControlProfile.FontEditorFields();
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

// While a scene is selected, show catalog field Field under Label. With Group set, the editor
// moves into that group directly after field After, which is then required and must be another
// field; without Group, the field is renamed in place and After must be null. An invalid
// placement changes nothing on the page (app.js sceneSections) and fails CommandBuilderTests.
internal sealed record ScenePlacement(string Field, string Label, string? Group = null, string? After = null, string? Help = null);
