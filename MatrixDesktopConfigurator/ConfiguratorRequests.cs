using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace MatrixDesktopConfigurator;

// Where every configurator page request is routed, and the parts of that routing that need no
// window. Free of WinForms, like PresetOperations, so the core harness runs the same code as
// ConfiguratorForm (R2-13): ConfiguratorHostTests sends every requestHost name in
// configurator/js/app.js through DispatchAsync, and CommandBuilderTests holds Use for
// screensaver to the preview.
internal static class ConfiguratorRequests
{
    // The requests ConfiguratorForm.ServeAsync answers itself: they need the window, a dialog,
    // the clipboard, the WebView, the media bridge or the Test process.
    public static readonly IReadOnlySet<string> FormTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "loadState", "saveDraft", "savePreset", "deletePreset", "buildCommand", "buildWebQuery", "importCommand",
        "browseConfig", "useScreensaver", "restoreScreensaver", "randomizeDraft", "browseMedia", "copyCommand",
        "testCommand", "stopTest", "setTheme", "exportPowerShell", "loadHelp",
    };

    // Requests answered here from the store alone, then those in FormTypes; any other type is
    // refused, and the page shows the refusal.
    public static async Task<object?> DispatchAsync(string type, JsonElement payload, ConfiguratorState state, StorageService storage,
        Func<string, JsonElement, Task<object?>> serveForm)
    {
        switch (type)
        {
            // AUD-02: name only; the saved values, the working draft and the selection stay.
            case "renamePreset":
                var preset = PresetOperations.Rename(state, payload, DateTimeOffset.UtcNow);
                storage.Save(state);
                return new { preset, state };
        }

        if (FormTypes.Contains(type)) return await serveForm(type, payload);
        throw new InvalidOperationException($"Unknown configurator request: {type}");
    }

    // Use for screensaver saves what the preview and Test render (AUD-13), not every default,
    // which wrote the catalog's stripe rows and base values over each version's own.
    public static string[] ScreensaverArguments(CommandBuilder builder, JsonObject draft) => builder.BuildScreensaverArguments(draft).ToArray();
}
