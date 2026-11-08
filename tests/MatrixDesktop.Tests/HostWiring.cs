using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace MatrixDesktop.Tests;

// Wiring checks on the published MatrixDesktop.dll, read from its IL without loading it. The
// pure host policies have their own tests; these prove the WinForms host actually calls them,
// which neither harness can link (MainForm needs WebView2).
internal static class HostWiring
{
    private sealed record Call(string Caller, int Offset, string Callee, int PreviousOpcode);

    private const string MessageBoxShow = "System.Windows.Forms.MessageBox::Show";
    private const string FormClose = "System.Windows.Forms.Form::Close";

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields()
        .Select(field => (OpCode)field.GetValue(null)!).GroupBy(code => code.Value).ToDictionary(group => group.Key, group => group.First());

    internal static int Verify(string path)
    {
        List<Call> calls;
        List<string> mainFormConstructors;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            calls = ReadCalls(pe, pe.GetMetadataReader());
            mainFormConstructors = InstanceConstructorAccess(pe.GetMetadataReader(), "MatrixDesktop.MainForm");
        }
        catch (Exception error)
        {
            Console.WriteLine($"FAIL  host wiring scan could not read {path}: {error.Message}");
            return 1;
        }

        var failures = 0;
        failures += Run($"host startup failures all route through FailStartup: {path}", () =>
        {
            var mainForm = calls.Where(call => call.Caller.StartsWith("MatrixDesktop.MainForm::", StringComparison.Ordinal)
                || call.Caller.StartsWith("MatrixDesktop.MainForm/", StringComparison.Ordinal)).ToList();
            var exits = mainForm.Where(call => call.Callee is MessageBoxShow or FormClose).ToList();
            Check.True(exits.Any(call => call.Callee == MessageBoxShow) && exits.Any(call => call.Callee == FormClose),
                "witness: the scan must see MainForm's error dialog and close at all");
            var stray = exits.Where(call => call.Caller != "MatrixDesktop.MainForm::FailStartup")
                .Select(call => $"{call.Caller} -> {call.Callee}").Distinct().ToArray();
            Check.True(stray.Length == 0, "every MainForm error dialog and close must go through FailStartup, which sets StartupFailed: " + string.Join("; ", stray));
            var callers = mainForm.Where(call => call.Callee == "MatrixDesktop.MainForm::FailStartup").Select(call => call.Caller).ToArray();
            Check.Equal(5, callers.Length, "the five renderer-start error exits must each call FailStartup: " + string.Join("; ", callers));
        });
        failures += Run($"FailStartup sets StartupFailed before its dialog and close: {path}", () =>
        {
            var body = calls.Where(call => call.Caller == "MatrixDesktop.MainForm::FailStartup").ToList();
            var set = body.FirstOrDefault(call => call.Callee == "MatrixDesktop.MainForm::set_StartupFailed");
            Check.True(set is not null && set.PreviousOpcode == OpCodes.Ldc_I4_1.Value, "FailStartup must set StartupFailed to true");
            var shown = body.Where(call => call.Callee is MessageBoxShow or FormClose).ToList();
            Check.Equal(2, shown.Count, "FailStartup must show exactly one dialog and close once");
            Check.True(shown.All(call => call.Offset > set!.Offset), "StartupFailed must be set before the dialog and the close");
        });
        failures += Run($"OnSurfaceClosed decides through SurfaceClosePolicy with StartupFailed: {path}", () =>
        {
            var body = calls.Where(call => call.Caller == "MatrixDesktop.MatrixApplicationContext::OnSurfaceClosed").ToList();
            var read = body.FirstOrDefault(call => call.Callee == "MatrixDesktop.MainForm::get_StartupFailed");
            var decide = body.FirstOrDefault(call => call.Callee == "MatrixDesktop.SurfaceClosePolicy::Decide");
            Check.True(read is not null, "OnSurfaceClosed must read the closing form's StartupFailed");
            Check.True(decide is not null && decide.Offset > read!.Offset, "OnSurfaceClosed must pass StartupFailed to SurfaceClosePolicy.Decide");
        });
        // AUD-31. A public MainForm constructor enabled a second, unreachable lock/sleep path of
        // MainForm's own; edits to it changed nothing, and reviving it would bypass PlaybackPolicy.
        failures += Run($"MainForm defines no public instance constructor: {path}", () =>
        {
            Check.True(mainFormConstructors.Count > 0, "witness: the scan must see MainForm's constructors at all");
            Check.False(mainFormConstructors.Contains("Public"), "only MatrixApplicationContext may construct MainForm: " + string.Join(", ", mainFormConstructors));
        });
        failures += Run($"SystemEvents session and power subscriptions have one owner: {path}", () =>
        {
            foreach (var subscribe in new[] { "Microsoft.Win32.SystemEvents::add_SessionSwitch", "Microsoft.Win32.SystemEvents::add_PowerModeChanged" })
            {
                var owners = calls.Where(call => call.Callee == subscribe).Select(call => call.Caller).ToArray();
                Check.Equal("MatrixDesktop.MatrixApplicationContext::.ctor", string.Join("; ", owners),
                    $"{subscribe} must have exactly one caller, the host context, which routes it through PlaybackPolicy");
            }
        });
        return failures;
    }

    // Accessibility of each instance constructor a type defines ("Assembly" for internal, "Public", ...).
    private static List<string> InstanceConstructorAccess(MetadataReader reader, string typeName) =>
        reader.TypeDefinitions.Select(reader.GetTypeDefinition).Where(type => TypeName(reader, type) == typeName)
            .SelectMany(type => type.GetMethods()).Select(reader.GetMethodDefinition)
            .Where(method => reader.GetString(method.Name) == ".ctor")
            .Select(method => (method.Attributes & System.Reflection.MethodAttributes.MemberAccessMask).ToString()).ToList();

    private static int Run(string name, Action check)
    {
        try { check(); Console.WriteLine($"PASS  {name}"); return 0; }
        catch (Exception error) { Console.WriteLine($"FAIL  {name}: {error.Message}"); return 1; }
    }

    private static List<Call> ReadCalls(PEReader pe, MetadataReader reader)
    {
        var calls = new List<Call>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var typeName = TypeName(reader, type);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) continue;
                var caller = typeName + "::" + reader.GetString(method.Name);
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
                var previous = -1;
                while (il.RemainingBytes > 0)
                {
                    var offset = il.Offset;
                    int value = il.ReadByte();
                    if (value == 0xFE) value = 0xFE00 | il.ReadByte();
                    var code = OpCodesByValue[unchecked((short)value)];
                    switch (code.OperandType)
                    {
                        case OperandType.InlineNone: break;
                        case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar: il.ReadByte(); break;
                        case OperandType.InlineVar: il.ReadInt16(); break;
                        case OperandType.InlineI8 or OperandType.InlineR: il.ReadInt64(); break;
                        case OperandType.InlineSwitch: var count = il.ReadInt32(); for (var i = 0; i < count; i++) il.ReadInt32(); break;
                        case OperandType.InlineMethod:
                            var token = il.ReadInt32();
                            if (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj)
                                calls.Add(new Call(caller, offset, MethodName(reader, MetadataTokens.EntityHandle(token)), previous));
                            break;
                        default: il.ReadInt32(); break;
                    }
                    previous = unchecked((short)value);
                }
            }
        }
        return calls;
    }

    private static string TypeName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return TypeName(reader, reader.GetTypeDefinition(declaring)) + "/" + name;
        var space = reader.GetString(type.Namespace);
        return space.Length == 0 ? name : space + "." + name;
    }

    private static string TypeReferenceName(MetadataReader reader, TypeReference type)
    {
        var name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeReferenceName(reader, reader.GetTypeReference((TypeReferenceHandle)type.ResolutionScope)) + "/" + name;
        var space = reader.GetString(type.Namespace);
        return space.Length == 0 ? name : space + "." + name;
    }

    private static string MethodName(MetadataReader reader, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                var definition = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return TypeName(reader, reader.GetTypeDefinition(definition.GetDeclaringType())) + "::" + reader.GetString(definition.Name);
            case HandleKind.MemberReference:
                var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
                var parent = reference.Parent.Kind switch
                {
                    HandleKind.TypeReference => TypeReferenceName(reader, reader.GetTypeReference((TypeReferenceHandle)reference.Parent)),
                    HandleKind.TypeDefinition => TypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)reference.Parent)),
                    _ => reference.Parent.Kind.ToString(),
                };
                return parent + "::" + reader.GetString(reference.Name);
            case HandleKind.MethodSpecification:
                return MethodName(reader, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
            default:
                return handle.Kind.ToString();
        }
    }
}
