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
        ("Copied commands keep text values literal in batch files and PowerShell", CopiedTextTransport),
        ("Shell-captured copied commands reach the renderer query as literal text", CopiedTextReachesRenderer),
        ("Copy then Import recovers literal text values", CopiedTextImports),
    ];

    // AUD-12. Double quotes do not stop a batch file expanding %2 in My%20Image.png, or
    // PowerShell expanding $old and a backtick, so BuildCommand sends a text value holding
    // %, !, $ or a backtick as json64 text. Each draft's copied line runs in a .bat under
    // cmd /d /c and a .ps1 under both PowerShells against the argv-capture stub; the three
    // cases below check the shell transport, the host decoder (AppCli -> MatrixArgs) and the
    // importer decoder separately. Draft C is a readable control: '&' alone stays quoted.
    // Killing mutations: narrow the encode condition back to font, characters and phrases
    // (transport fails for batch A and PowerShell B and D); remove the MatrixArgs decode
    // (only the renderer case fails); remove the importer decode (only the import case fails).
    // R2-12: removing the apostrophe, ';', the braces or '#' from CommandBuilder.NeedsQuoting
    // fails only draft F, G, H or I under both PowerShells; removing '@' fails J under
    // PowerShell 7 and makes Windows PowerShell refuse the whole script.
    private static readonly string[] Shells = ["cmd.exe", "powershell.exe", "pwsh.exe"];
    private static readonly Lazy<Dictionary<string, string[]>> TextCaptures = new(CaptureCopiedTextCommands);

    private static (string Name, JsonObject Draft, string Key)[] TextDrafts(CommandBuilder builder)
    {
        var a = builder.CreateDefaultDraft();
        a["effect"] = "image";
        a["url"] = "https://example.test/images/My%20Image.png";
        var b = builder.CreateSceneDefaultDraft("uc3d");
        b["showPictures"] = true;
        b["pictureSource"] = "custom";
        b["pictureDirectory"] = @"C:\Users\Neo\Pictures\$old";
        var c = builder.CreateDefaultDraft();
        c["effect"] = "image";
        c["url"] = "https://example.test/a&b.png";
        var d = builder.CreateSceneDefaultDraft("uc3d");
        d["sound"] = "music";
        d["musicFile"] = @"C:\Music\a`tb.mp3";
        var e = builder.CreateDefaultDraft();
        e["effect"] = "image";
        e["url"] = "https://example.test/wow!.png";
        // R2-12: bare, PowerShell pairs these two apostrophes into one argument, ends the
        // statement at ';', swallows braces as a script block, reads a leading '#' as a comment
        // and a leading '@' as splatting. A lone apostrophe or brace (and, in Windows
        // PowerShell, that '@') refuses the whole shared script, so F and H keep theirs paired.
        var f = builder.CreateSceneDefaultDraft("uc3d");
        f["showPictures"] = true;
        f["pictureSource"] = "custom";
        f["pictureDirectory"] = @"C:\Users\O'Neil\Pictures";
        f["sound"] = "music";
        f["musicFile"] = @"C:\Users\O'Neil\Music\a.mp3";
        var g = builder.CreateDefaultDraft();
        g["effect"] = "image";
        g["url"] = "https://example.test/a;b.png";
        var h = builder.CreateDefaultDraft();
        h["effect"] = "image";
        h["url"] = "https://example.test/{id}.png";
        var i = builder.CreateSceneDefaultDraft("uc3d");
        i["sound"] = "music";
        i["musicFile"] = "#1.mp3";
        var j = builder.CreateDefaultDraft();
        j["effect"] = "image";
        j["url"] = "@home.png";
        return [("A", a, "url"), ("B", b, "pictureDirectory"), ("C", c, "url"), ("D", d, "musicFile"), ("E", e, "url"),
            ("F", f, "musicFile"), ("G", g, "url"), ("H", h, "url"), ("I", i, "musicFile"), ("J", j, "url")];
    }

    private static void CopiedTextTransport()
    {
        var builder = new CommandBuilder(ArgumentCatalog.Create());
        var failures = new List<string>();
        foreach (var shell in Shells)
        foreach (var (name, draft, key) in TextDrafts(builder))
        {
            var decoded = TextCaptures.Value[shell + " " + name].Select(global::Shared.StructuredArgumentCodec.DecodeText).ToArray();
            if (!builder.BuildArguments(draft, false, false).SequenceEqual(decoded))
                failures.Add($"{shell} {name} ({key}): [{string.Join(" | ", decoded)}]");
        }
        Check.True(failures.Count == 0, "a pasted copied command must deliver every value literally: " + string.Join("; ", failures));
        var drafts = TextDrafts(builder);
        Check.DoesNotContain(builder.BuildCommand(drafts[2].Draft, false, false), "json64:", "a value without %, !, $ or a backtick stays readable");
        foreach (var (name, draft, _) in drafts)
        {
            Check.False(builder.BuildArguments(draft, false, false).Any(token => token.StartsWith("json64:", StringComparison.Ordinal)), "Test, Export .ps1 and the screensaver keep raw argv: " + name);
            Check.DoesNotContain(builder.BuildWebQueryString(draft), "json64", "the live preview query stays raw: " + name);
        }
    }

    private static void CopiedTextReachesRenderer()
    {
        var builder = new CommandBuilder(ArgumentCatalog.Create());
        var failures = new List<string>();
        foreach (var shell in Shells)
        foreach (var (name, draft, key) in TextDrafts(builder))
        {
            AppCli.Parse(TextCaptures.Value[shell + " " + name], out var passthrough);
            var query = MatrixArgs.BuildQueryString(passthrough).Split('&').Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair.Length > 1 ? pair[1] : string.Empty));
            var arrived = query.GetValueOrDefault(key) ?? "(missing)";
            if (arrived != (string)draft[key]!) failures.Add($"{shell} {name}: {key}={arrived}");
        }
        Check.True(failures.Count == 0, "the renderer and the bridge's folder lookups must see each literal value: " + string.Join("; ", failures));
    }

    private static void CopiedTextImports()
    {
        var metadata = ArgumentCatalog.Create();
        var builder = new CommandBuilder(metadata);
        var importer = new ArgumentImporter(metadata);
        foreach (var (name, draft, key) in TextDrafts(builder))
        {
            var imported = importer.Import(builder.BuildCommand(draft, false, false), builder.CreateDefaultDraft());
            Check.Equal((string)draft[key]!, (string)imported.Draft[key]!, $"Copy then Import must recover draft {name}'s {key}");
            Check.True(imported.Ignored.Length == 0, $"draft {name} must import without ignored keys: {string.Join(", ", imported.Ignored)}");
        }
    }

    private static Dictionary<string, string[]> CaptureCopiedTextCommands()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "matrix-export-tests-copied-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var testBase = AppContext.BaseDirectory;
            File.Copy(Path.Combine(testBase, "MatrixDesktop.Tests.exe"), Path.Combine(fixture, "MatrixDesktop.exe"));
            foreach (var name in new[] { "MatrixDesktop.Tests.dll", "MatrixDesktop.Tests.deps.json", "MatrixDesktop.Tests.runtimeconfig.json" })
                File.Copy(Path.Combine(testBase, name), Path.Combine(fixture, name));
            var builder = new CommandBuilder(ArgumentCatalog.Create());
            var drafts = TextDrafts(builder);
            var captures = new Dictionary<string, string[]>();
            foreach (var shell in Shells)
            {
                var batch = shell == "cmd.exe";
                var script = new StringBuilder(batch ? "@echo off\r\n" : string.Empty);
                foreach (var (name, draft, _) in drafts)
                {
                    var command = builder.BuildCommand(draft, false, false);
                    Check.True(command.StartsWith("MatrixDesktop.exe ", StringComparison.Ordinal), "a copied command starts with the executable name");
                    var capture = Path.Combine(fixture, shell + "-" + name + ".json");
                    script.Append(batch ? $"set \"MATRIXDESKTOP_TEST_ARGV_CAPTURE={capture}\"\r\n" : $"$env:MATRIXDESKTOP_TEST_ARGV_CAPTURE = '{capture}'\r\n");
                    // Agent shells skip the current directory, so name the stub by path, as a
                    // PowerShell user must anyway.
                    script.Append(@".\").Append(command).Append("\r\n");
                }
                var path = Path.Combine(fixture, batch ? "copied.bat" : "copied-" + shell + ".ps1");
                File.WriteAllText(path, script.ToString(), Encoding.ASCII);
                var start = new ProcessStartInfo(shell)
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                foreach (var argument in batch ? new[] { "/d", "/c", path } : new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path })
                    start.ArgumentList.Add(argument);
                start.Environment.Remove("MATRIXDESKTOP_TEST_ARGV_CAPTURE");
                using var process = Process.Start(start) ?? throw new InvalidOperationException(shell + " did not start.");
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30_000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new InvalidOperationException(shell + " timed out running the copied commands."); }
                foreach (var (name, _, _) in drafts)
                {
                    var capture = Path.Combine(fixture, shell + "-" + name + ".json");
                    Check.True(File.Exists(capture), $"{shell} must run the copied command for draft {name}: {output.Result} {errors.Result}");
                    captures[shell + " " + name] = JsonNode.Parse(File.ReadAllText(capture))!["arguments"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
                }
            }
            return captures;
        }
        finally
        {
            var absolute = Path.GetFullPath(fixture);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("matrix-export-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an unexpected export fixture path.");
            var cleanupDeadline = Stopwatch.StartNew();
            while (Directory.Exists(absolute))
            {
                try { Directory.Delete(absolute, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException && cleanupDeadline.ElapsedMilliseconds < 5_000)
                { Thread.Sleep(50); }
            }
        }
    }

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
