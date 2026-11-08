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
    private sealed record Call(string Caller, int Offset, string Callee, int PreviousOpcode)
    {
        // The opcode right after the call, so a check can see what the result feeds.
        internal int NextOpcode { get; init; } = -1;
    }

    private const string MessageBoxShow = "System.Windows.Forms.MessageBox::Show";
    private const string FormClose = "System.Windows.Forms.Form::Close";

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields()
        .Select(field => (OpCode)field.GetValue(null)!).GroupBy(code => code.Value).ToDictionary(group => group.Key, group => group.First());

    internal static int Verify(string path)
    {
        List<Call> calls;
        List<Call> functionLoads = [];
        List<string> mainFormConstructors;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            calls = ReadCalls(pe, pe.GetMetadataReader(), functionLoads);
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
        // AUD-72. WallpaperInstanceGuard has its own tests; these prove the wallpaper context uses it.
        failures += Run($"wallpaper launch takes the instance guard before its tray and releases it on exit: {path}", () =>
        {
            var ctor = calls.Where(call => call.Caller == "MatrixDesktop.MatrixApplicationContext::.ctor").ToList();
            var take = ctor.FirstOrDefault(call => call.Callee == "MatrixDesktop.MatrixApplicationContext::TakeWallpaperOwnership");
            var tray = ctor.FirstOrDefault(call => call.Callee == "MatrixDesktop.MatrixApplicationContext::CreateTray");
            Check.True(tray is not null, "witness: the scan must see the constructor create the tray");
            Check.True(take is not null && take.Offset < tray!.Offset, "the wallpaper context must take the instance guard before it creates its tray");
            var taking = calls.Where(call => call.Caller == "MatrixDesktop.MatrixApplicationContext::TakeWallpaperOwnership").Select(call => call.Callee).ToList();
            Check.True(taking.Contains("MatrixDesktop.WallpaperInstanceGuard::Acquire") && taking.Contains("MatrixDesktop.WallpaperInstanceGuard::ListenForTakeover"),
                "taking ownership must acquire the guard and listen for a newer launch");
            Check.True(calls.Any(call => call.Caller == "MatrixDesktop.MatrixApplicationContext::ExitThreadCore" && call.Callee == "MatrixDesktop.WallpaperInstanceGuard::Dispose"),
                "the context must release the guard when it exits, or a newer launch waits out its bound");
        });
        // R2-02. CreateTray subscribes Opening twice (Refresh desktop visibility, AUD-09, and this
        // refresh), so "CreateTray calls add_Opening" passed with this refresh deleted. Resolve the
        // method each Opening delegate is built from and require one to read AutostartService.
        failures += Run($"the tray refreshes Start with Windows when its menu opens: {path}", () =>
        {
            var handlers = DelegateTargets(calls, functionLoads, "MatrixDesktop.MatrixApplicationContext::CreateTray",
                "System.ComponentModel.CancelEventHandler::.ctor", "System.Windows.Forms.ToolStripDropDown::add_Opening");
            Check.True(handlers.Count > 0, "witness: the scan must resolve the handler of each Opening subscription in CreateTray");
            Check.True(handlers.Any(handler => Reaches(calls, handler, ReadsAutostart, depth: 2)),
                "an Opening handler in CreateTray must read AutostartService, so a shortcut that another launch or Explorer changed shows when the menu opens: "
                + string.Join("; ", handlers));
        });
        // AUD-07. RendererFailurePolicy has its own tests; these prove WebView2 failures reach it.
        failures += Run($"WebView2 process failures reach RendererFailurePolicy: {path}", () =>
        {
            Check.True(calls.Any(call => call.Caller.StartsWith("MatrixDesktop.MainForm", StringComparison.Ordinal) && call.Callee == "Microsoft.Web.WebView2.Core.CoreWebView2::add_ProcessFailed"),
                "MainForm must subscribe CoreWebView2.ProcessFailed");
            Check.True(calls.Any(call => call.Caller == "MatrixDesktop.MainForm::DetachWebViewHandlers" && call.Callee == "Microsoft.Web.WebView2.Core.CoreWebView2::remove_ProcessFailed"),
                "MainForm must detach its ProcessFailed handler with the others");
            Check.True(calls.Any(call => call.Caller == "MatrixDesktop.MatrixApplicationContext::TryCreateSurface" && call.Callee == "MatrixDesktop.MainForm::add_RendererFailed"),
                "the context must subscribe every surface's RendererFailed");
            var handler = calls.Where(call => call.Caller == "MatrixDesktop.MatrixApplicationContext::OnRendererFailed").Select(call => call.Callee).ToList();
            Check.True(handler.Contains("MatrixDesktop.RendererFailurePolicy::Decide") && handler.Contains("MatrixDesktop.MainForm::ReloadRenderer"),
                "the context must decide through RendererFailurePolicy and reload through the surface");
            Check.True(handler.Contains("MatrixDesktop.SurfaceClosePolicy::RetryDelayMs"),
                "a lost renderer must be rebuilt after the capped backoff, never in a tight loop");
            Check.True(calls.Any(call => call.Caller == "MatrixDesktop.MatrixApplicationContext::OnTick" && call.Callee == "MatrixDesktop.RendererFailurePolicy::StayedHealthy"),
                "the wallpaper tick must reset the rebuild backoff only once a renderer stayed ready");
        });
        // R2-01. HostClosePolicy has its own test; this proves OnFormClosing branches on it, with
        // the flag WndProc sets, before any teardown. The cancelled-shutdown leg itself is native.
        failures += Run($"a session-end query reaches HostClosePolicy before MainForm tears down: {path}", () =>
        {
            var body = calls.Where(call => call.Caller == "MatrixDesktop.MainForm::OnFormClosing").ToList();
            var decide = body.FirstOrDefault(call => call.Callee == "MatrixDesktop.HostClosePolicy::TearsDownOnClosing");
            var teardown = body.Where(call => call.Callee is "MatrixDesktop.MainForm::TryCleanupWebView" or "MatrixDesktop.Shared.NativeMediaBridge::Dispose"
                or "MatrixDesktop.MainForm::StopForegroundEnforcer").ToList();
            Check.True(teardown.Any(call => call.Callee == "MatrixDesktop.MainForm::TryCleanupWebView"), "witness: the scan must see OnFormClosing clean up the WebView at all");
            Check.True(decide is not null, "OnFormClosing must ask HostClosePolicy whether this close tears down");
            Check.True(teardown.All(call => call.Offset > decide!.Offset), "OnFormClosing must ask HostClosePolicy before it stops anything: "
                + string.Join("; ", teardown.Where(call => call.Offset < decide!.Offset).Select(call => call.Callee)));
            Check.True(decide!.PreviousOpcode == OpCodes.Ldfld.Value, "HostClosePolicy must be given the field WndProc sets, not a constant");
            Check.True(decide.NextOpcode is 0x2C or 0x2D or 0x39 or 0x3A, $"OnFormClosing must branch on HostClosePolicy's answer (next opcode 0x{decide.NextOpcode:X2})");
            Check.True(calls.Any(call => call.Caller == "MatrixDesktop.MainForm::WndProc" && call.Callee == "System.Windows.Forms.Form::WndProc"),
                "MainForm must mark the WM_QUERYENDSESSION dispatch in its own WndProc");
        });
        return failures;
    }

    // Accessibility of each instance constructor a type defines ("Assembly" for internal, "Public", ...).
    private static List<string> InstanceConstructorAccess(MetadataReader reader, string typeName) =>
        reader.TypeDefinitions.Select(reader.GetTypeDefinition).Where(type => TypeName(reader, type) == typeName)
            .SelectMany(type => type.GetMethods()).Select(reader.GetMethodDefinition)
            .Where(method => reader.GetString(method.Name) == ".ctor")
            .Select(method => (method.Attributes & System.Reflection.MethodAttributes.MemberAccessMask).ToString()).ToList();

    // The method each delegate passed to subscribe in caller is built from: the nearest ldftn
    // before the subscribe call, with the delegate's constructor between them.
    private static List<string> DelegateTargets(List<Call> calls, List<Call> functionLoads, string caller, string delegateConstructor, string subscribe) =>
        calls.Where(call => call.Caller == caller && call.Callee == subscribe).Select(call =>
        {
            var constructed = calls.LastOrDefault(made => made.Caller == caller && made.Callee == delegateConstructor && made.Offset < call.Offset);
            var loaded = constructed is null ? null : functionLoads.LastOrDefault(load => load.Caller == caller && load.Offset < constructed.Offset);
            return loaded?.Callee;
        }).OfType<string>().ToList();

    // True when method, or a MatrixDesktop method it calls within depth steps, calls a matching callee.
    private static bool Reaches(List<Call> calls, string method, Func<string, bool> matches, int depth)
    {
        var callees = calls.Where(call => call.Caller == method).Select(call => call.Callee).Distinct().ToList();
        return callees.Any(matches) || depth > 1 && callees.Where(callee => callee.StartsWith("MatrixDesktop.", StringComparison.Ordinal) && callee != method)
            .Any(callee => Reaches(calls, callee, matches, depth - 1));
    }

    // Reads, never writes: Enabled, or the State family that compares the shortcut with this launch.
    private static bool ReadsAutostart(string callee) =>
        callee == "MatrixDesktop.AutostartService::get_Enabled" || callee.StartsWith("MatrixDesktop.AutostartService::State", StringComparison.Ordinal);

    private static int Run(string name, Action check)
    {
        try { check(); Console.WriteLine($"PASS  {name}"); return 0; }
        catch (Exception error) { Console.WriteLine($"FAIL  {name}: {error.Message}"); return 1; }
    }

    private static List<Call> ReadCalls(PEReader pe, MetadataReader reader, List<Call> functionLoads)
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
                var awaitingNext = -1;
                while (il.RemainingBytes > 0)
                {
                    var offset = il.Offset;
                    int value = il.ReadByte();
                    if (value == 0xFE) value = 0xFE00 | il.ReadByte();
                    if (awaitingNext >= 0) { calls[awaitingNext] = calls[awaitingNext] with { NextOpcode = unchecked((short)value) }; awaitingNext = -1; }
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
                            {
                                calls.Add(new Call(caller, offset, MethodName(reader, MetadataTokens.EntityHandle(token)), previous));
                                awaitingNext = calls.Count - 1;
                            }
                            else if (code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn)
                                functionLoads.Add(new Call(caller, offset, MethodName(reader, MetadataTokens.EntityHandle(token)), previous));
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
