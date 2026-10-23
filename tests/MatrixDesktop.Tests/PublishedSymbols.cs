using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace MatrixDesktop.Tests;

internal static class PublishedSymbols
{
    public static int Verify(string[] paths)
    {
        var failures = 0;
        foreach (var path in paths)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                var entries = pe.ReadDebugDirectory().Where(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).ToArray();
                Check.Equal(1, entries.Length, $"{path} must carry embedded symbols");
                using var pdb = pe.ReadEmbeddedPortablePdbDebugDirectoryData(entries[0]);
                var reader = pdb.GetMetadataReader();
                Check.True(reader.MethodDebugInformation.Any(handle => reader.GetMethodDebugInformation(handle).GetSequencePoints().Any(point => !point.IsHidden)),
                    $"{path} must carry source line mappings");

                var context = new AssemblyLoadContext("symbols-" + Guid.NewGuid(), isCollectible: true);
                try
                {
                    var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
                    string sourceFile;
                    Action trigger;
                    if (Path.GetFileNameWithoutExtension(path) == "MatrixDesktop")
                    {
                        sourceFile = "MatrixArgs.cs";
                        var method = assembly.GetType("MatrixDesktop.MatrixArgs", throwOnError: true)!.GetMethod("BuildQueryString")!;
                        trigger = () => method.Invoke(null, [new ThrowingArguments()]);
                    }
                    else
                    {
                        sourceFile = "ArgumentImporter.cs";
                        var metadata = assembly.GetType("MatrixDesktopConfigurator.ArgumentCatalog", true)!.GetMethod("Create")!.Invoke(null, null)!;
                        var builder = Activator.CreateInstance(assembly.GetType("MatrixDesktopConfigurator.CommandBuilder", true)!, [metadata])!;
                        var draft = builder.GetType().GetMethod("CreateDefaultDraft")!.Invoke(builder, null)!;
                        var importer = Activator.CreateInstance(assembly.GetType("MatrixDesktopConfigurator.ArgumentImporter", true)!, [metadata])!;
                        trigger = () => importer.GetType().GetMethod("Import")!.Invoke(importer, ["   ", draft]);
                    }

                    try { trigger(); throw new InvalidOperationException("symbol witness did not throw"); }
                    catch (TargetInvocationException exception)
                    {
                        var trace = exception.InnerException?.ToString() ?? string.Empty;
                        Check.Contains(trace, sourceFile + ":line ", $"{path} must produce source lines in a real exception stack");
                    }
                }
                finally { context.Unload(); }
                Console.WriteLine($"PASS  published exception stack has source lines: {path}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.WriteLine($"FAIL  published exception stack has source lines: {path}: {exception.Message}");
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private sealed class ThrowingArguments : IReadOnlyList<string>
    {
        public int Count => 1;
        public string this[int index] => throw new InvalidOperationException("published stack trace witness");
        public IEnumerator<string> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
