using System.Text.Json;
using MatrixDesktopConfigurator;

namespace MatrixDesktop.Tests;

// Produces the exact payload shape ConfiguratorForm.LoadStatePayload returns, so the headless
// web smoke exercises the real argument catalogue instead of a fixture someone typed out once.
//
// Only the two fields the page cannot boot without come from production code: metadata and
// defaultDraft. The rest is a minimal empty-user shell, deliberately, because the smoke asserts
// first-run layout and a saved preset or a stashed draft would make the run depend on machine
// state. preview.available is false so no iframe navigates to a virtual host that does not
// exist headlessly; the smoke asserts the preview pane's box, which is laid out either way.
internal static class FixtureDump
{
    public static void WriteLoadStatePayload(string path)
    {
        var metadata = ArgumentCatalog.Create();
        var builder = new CommandBuilder(metadata);

        var payload = new
        {
            metadata,
            defaultDraft = builder.CreateDefaultDraft(),
            state = new
            {
                userPresets = Array.Empty<object>(),
                selectedPresetId = (string?)null,
                lastDraft = builder.CreateDefaultDraft(),
                uiTheme = "dark",
            },
            storage = new { path = "(headless smoke)", portable = false },
            preview = new { available = false, origin = (string?)null },
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            // Web defaults, matching WebView2's own camelCase contract. Without this the page
            // would receive PascalCase keys, read undefined everywhere, and the smoke would
            // fail for a reason that has nothing to do with the product.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, json);
    }
}
