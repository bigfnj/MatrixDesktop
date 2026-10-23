using MatrixDesktop.Tests;
using MatrixDesktopConfigurator;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length >= 2 && args[0] == "--verify-published-symbols")
{
    return PublishedSymbols.Verify(args[1..]);
}

// Secondary mode: write the loadState payload the configurator's WebView2 host would send,
// so tests\web-smoke.py can drive configurator\index.html in headless Chromium without a
// WebView2 host at all.
//
// Built here, from the same ArgumentCatalog and CommandBuilder the product uses, rather than
// hand-written as a fixture. A hand-written fixture would drift from the catalog silently and
// the headless smoke would then be asserting layout against a page the product never renders.
if (args.Length == 2 && args[0] == "--dump-state")
{
    FixtureDump.WriteLoadStatePayload(args[1]);
    Console.WriteLine($"wrote loadState fixture to {args[1]}");
    return 0;
}

// The DOM smoke uses the same pure importer/builder as the real host. Requests
// arrive on stdin, so JSON font and phrase payloads do not depend on shell quoting.
if (args.Length == 1 && args[0] == "--configurator-request")
{
    Console.InputEncoding = new System.Text.UTF8Encoding(false);
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    try
    {
    var request = JsonNode.Parse(Console.In.ReadToEnd())!.AsObject();
    var type = request["type"]!.GetValue<string>();
    var payload = request["payload"]?.AsObject() ?? new JsonObject();
    var metadata = ArgumentCatalog.Create();
    var builder = new CommandBuilder(metadata);
    var draft = payload["draft"]?.AsObject() ?? builder.CreateDefaultDraft();
    object result;
    if (type is "buildCommand" or "testCommand")
    {
        var test = type == "testCommand";
        result = new { command = test ? builder.BuildTestCommandDescription(draft) : builder.BuildCommand(draft, false, false), arguments = builder.BuildArguments(draft, false, test), processId = 123 };
    }
    else if (type == "importCommand")
    {
        var command = payload["command"]!.GetValue<string>();
        var imported = new ArgumentImporter(metadata).Import(command, ZMatrixCfgImporter.IsConfiguration(command) ? draft : builder.CreateDefaultDraft());
        result = new { draft = imported.Draft, applied = imported.Applied, ignored = imported.Ignored, notes = imported.Notes };
    }
    else if (type == "buildWebQuery") result = new { query = builder.BuildWebQueryString(draft) };
    else throw new ArgumentException("Unknown synthetic configurator request: " + type);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

// Console harness: the exit code is the result. Test names are full sentences describing
// the behaviour, so a failure line reads as a statement of what broke.
List<(string Name, Action Test)> tests = [];
tests.AddRange(MatrixArgsTests.All);
tests.AddRange(AppCliTests.All);
tests.AddRange(ImporterTests.All);
tests.AddRange(CommandBuilderTests.All);
tests.AddRange(ParityTests.All);
tests.AddRange(GuideTests.All);
tests.AddRange(StorageTests.All);
tests.AddRange(MediaCatalogTests.All);
tests.AddRange(ZMatrixTests.All);
tests.AddRange(ScreensaverRegistrationTests.All);
tests.AddRange(PlaybackPolicyTests.All);
if (args.Length == 2 && args[0] == "--only")
    tests = tests.Where(test => test.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToList();
if (tests.Count == 0) throw new ArgumentException("The requested test filter matched no tests.");

var failures = 0;
foreach ((string name, Action test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"FAIL  {name}: {exception.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed.");
return failures == 0 ? 0 : 1;
