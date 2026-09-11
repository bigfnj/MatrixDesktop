using MatrixDesktop.Tests;

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
