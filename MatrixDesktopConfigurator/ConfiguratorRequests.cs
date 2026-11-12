using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MatrixDesktopConfigurator;

// Where every configurator page request is routed. Free of WinForms, like PresetOperations, so
// the core harness runs this switch itself (R2-13, R3-08): ConfiguratorHostTests sends every
// requestHost name in configurator/js/app.js through DispatchAsync to a recording window, and
// CommandBuilderTests reads the argv Use for screensaver hands that window. ConfiguratorForm, the
// real IConfiguratorWindow, compiles in no test project, so what each of its methods does is
// covered only by the native smoke and the real-app check.
internal static class ConfiguratorRequests
{
    // renamePreset is answered here from the store alone; every other request the page posts
    // reaches the window method of its own name; any other type is refused, and the page shows
    // the refusal.
    public static async Task<object?> DispatchAsync(string type, JsonElement payload, ConfiguratorState state, StorageService storage,
        CommandBuilder builder, IConfiguratorWindow window)
    {
        switch (type)
        {
            // AUD-02: name only; the saved values, the working draft and the selection stay.
            case "renamePreset":
                var preset = PresetOperations.Rename(state, payload, DateTimeOffset.UtcNow);
                storage.Save(state);
                return new { preset, state };
            // Use for screensaver saves what the preview and Test render (AUD-13), not every
            // default, which wrote the catalog's stripe rows and base values over each version's
            // own. The window gets this argv, never the draft.
            case "useScreensaver":
                return window.UseScreensaver(builder.BuildScreensaverArguments(PresetOperations.ReadObject(payload, "draft")).ToArray());
        }

        return type switch
        {
            "loadState"          => window.LoadState(),
            "saveDraft"          => window.SaveDraft(payload),
            "savePreset"         => window.SavePreset(payload),
            "deletePreset"       => window.DeletePreset(payload),
            "buildCommand"       => window.BuildCommand(payload),
            "buildWebQuery"      => await window.BuildWebQueryAsync(payload),
            "importCommand"      => window.ImportCommand(payload),
            "browseConfig"       => window.BrowseConfig(),
            "restoreScreensaver" => window.RestoreScreensaver(),
            "randomizeDraft"     => window.RandomizeDraft(payload),
            "browseMedia"        => window.BrowseMedia(payload),
            "copyCommand"        => window.CopyCommand(payload),
            "testCommand"        => window.TestCommand(payload),
            "stopTest"           => window.StopTest(),
            "setTheme"           => window.SetTheme(payload),
            "exportPowerShell"   => window.ExportPowerShell(payload),
            "loadHelp"           => window.LoadHelp(),
            _ => throw new InvalidOperationException($"Unknown configurator request: {type}"),
        };
    }
}

// The requests that need the window: a dialog, the clipboard, the WebView, the media bridge,
// the Test process, the screensaver registration or the form's state. One method per request,
// named after it (ConfiguratorHostTests holds the names to the page's).
internal interface IConfiguratorWindow
{
    object LoadState();
    object SaveDraft(JsonElement payload);
    object SavePreset(JsonElement payload);
    object DeletePreset(JsonElement payload);
    object BuildCommand(JsonElement payload);
    Task<object> BuildWebQueryAsync(JsonElement payload);
    object ImportCommand(JsonElement payload);
    object BrowseConfig();
    object UseScreensaver(string[] screensaverArguments);
    object RestoreScreensaver();
    object RandomizeDraft(JsonElement payload);
    object BrowseMedia(JsonElement payload);
    object CopyCommand(JsonElement payload);
    object TestCommand(JsonElement payload);
    object StopTest();
    object SetTheme(JsonElement payload);
    object ExportPowerShell(JsonElement payload);
    object LoadHelp();
}
