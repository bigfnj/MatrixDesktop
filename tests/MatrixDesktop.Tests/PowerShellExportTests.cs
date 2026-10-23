using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MatrixDesktopConfigurator;

namespace MatrixDesktop.Tests;

internal static class PowerShellExportTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("PowerShell exports preserve real native argv under Windows PowerShell 5.1", () => RoundTrip("powershell.exe")),
        ("PowerShell exports preserve real native argv under PowerShell 7", () => RoundTrip("pwsh.exe")),
    ];

    private static void RoundTrip(string shell)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "matrix-export-tests-Ω O'Brien &= " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var testBase = AppContext.BaseDirectory;
            File.Copy(Path.Combine(testBase, "MatrixDesktop.Tests.exe"), Path.Combine(fixture, "MatrixDesktop.exe"));
            foreach (var name in new[] { "MatrixDesktop.Tests.dll", "MatrixDesktop.Tests.deps.json", "MatrixDesktop.Tests.runtimeconfig.json" })
                File.Copy(Path.Combine(testBase, name), Path.Combine(fixture, name));
            var builder = new CommandBuilder(ArgumentCatalog.Create());
            var text = builder.CreateDefaultDraft();
            text["effect"] = "image";
            text["url"] = "it's = %PATH% ! & $ HOME Ω Ж カ C:\\folder with spaces\\\"quote\"\\";
            var structured = builder.CreateSceneDefaultDraft("zmatrix");
            structured["zmPhrases"] = new JsonArray("A=B", "The \"matrix\" has C:\\code\\", "comma, ; & % ! $ ' Ω Ж カ");
            structured["zmCharacters"] = new JsonArray("A", "=", "\"", "\\", "Ω", " ");
            structured["zmFont"]!["name"] = "Face = ' Ω \\";
            var emptyPhrases = builder.CreateSceneDefaultDraft("zmatrix");
            emptyPhrases["zmPhrases"] = new JsonArray();
            var large = builder.CreateSceneDefaultDraft("zmatrix");
            large["zmPhrases"] = new JsonArray(Enumerable.Range(0, 64)
                .Select(index => (JsonNode?)JsonValue.Create(index + "=" + new string('Ω', 250))).ToArray());
            foreach (var (name, draft) in new[]
            {
                ("empty-argv", builder.CreateDefaultDraft()), ("plain-text", text),
                ("structured", structured), ("empty-phrases", emptyPhrases), ("large-unicode", large),
            })
            {
                var script = Path.Combine(fixture, name + ".ps1");
                var capture = Path.Combine(fixture, name + ".json");
                var expected = builder.BuildArguments(draft, false, false).ToArray();
                File.WriteAllText(script, builder.BuildPowerShellScript(draft, false), new UTF8Encoding(false));
                var start = new ProcessStartInfo(shell)
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(argument);
                start.Environment["MATRIXDESKTOP_TEST_ARGV_CAPTURE"] = capture;
                using var process = Process.Start(start) ?? throw new InvalidOperationException("The PowerShell export fixture did not start.");
                if (!process.WaitForExit(15_000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new InvalidOperationException("The PowerShell export fixture timed out."); }
                Check.Equal(0, process.ExitCode, shell + " must execute its generated script: " + process.StandardError.ReadToEnd());
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(capture) && deadline.ElapsedMilliseconds < 10_000) Thread.Sleep(20);
                Check.True(File.Exists(capture), shell + " must launch the real argv capture process: " + name);
                var result = JsonNode.Parse(File.ReadAllText(capture))!;
                var captured = result["arguments"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
                try { using var child = Process.GetProcessById(result["processId"]!.GetValue<int>()); Check.True(child.WaitForExit(10_000), "the owned argv capture process must exit"); }
                catch (ArgumentException) { } // The capture process normally exits before it is inspected.
                Check.True(expected.SequenceEqual(captured), shell + " must preserve exact native argv for " + name);
                Check.Equal(Path.Combine(fixture, "MatrixDesktop.exe"), result["executable"]!.GetValue<string>(), "the exported script must resolve its executable beside itself, independent of caller CWD");
            }
        }
        finally
        {
            var absolute = Path.GetFullPath(fixture);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("matrix-export-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an unexpected export fixture path.");
            // Windows can retain the image section briefly after process exit.
            var cleanupDeadline = Stopwatch.StartNew();
            while (Directory.Exists(absolute))
            {
                try { Directory.Delete(absolute, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException && cleanupDeadline.ElapsedMilliseconds < 5_000)
                { Thread.Sleep(50); }
            }
        }
    }
}
