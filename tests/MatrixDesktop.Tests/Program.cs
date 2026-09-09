using MatrixDesktop.Tests;

// Console harness: the exit code is the result. Test names are full sentences describing
// the behaviour, so a failure line reads as a statement of what broke.
List<(string Name, Action Test)> tests = [];
tests.AddRange(MatrixArgsTests.All);
tests.AddRange(AppCliTests.All);
tests.AddRange(ImporterTests.All);
tests.AddRange(CommandBuilderTests.All);
tests.AddRange(ParityTests.All);
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
