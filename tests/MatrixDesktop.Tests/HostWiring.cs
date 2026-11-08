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
        // R2-54. OnRendererFailed releases the wallpaper mouse hook before its surface goes;
        // RecoverSurface did not, so the rebuild's synchronous desktop capture ran with the hook
        // installed and every mouse event waited on the blocked UI thread. The ldc.i4.0 right
        // before the call is the false argument.
        failures += Run($"a surface rebuild releases the wallpaper mouse hook first: {path}", () =>
        {
            const string setEnabled = "MatrixDesktop.DesktopPointerBridge::SetEnabled";
            static bool Releases(Call call) => call.Callee == setEnabled && call.PreviousOpcode == OpCodes.Ldc_I4_0.Value;
            Check.True(calls.Any(call => call.Caller == "MatrixDesktop.MatrixApplicationContext::OnRendererFailed" && Releases(call)),
                "witness: the scan must see OnRendererFailed release the hook with SetEnabled(false)");
            var body = calls.Where(call => call.Caller == "MatrixDesktop.MatrixApplicationContext::RecoverSurface").ToList();
            var close = body.FirstOrDefault(call => call.Callee == FormClose);
            var rebuild = body.FirstOrDefault(call => call.Callee == "MatrixDesktop.MatrixApplicationContext::TryCreateSurface");
            Check.True(close is not null && rebuild is not null, "witness: the scan must see RecoverSurface close the old surface and build the new one");
            var release = body.FirstOrDefault(Releases);
            Check.True(release is not null, "RecoverSurface must release the wallpaper mouse hook with SetEnabled(false)");
            Check.True(release!.Offset < close!.Offset && release.Offset < rebuild!.Offset,
                "RecoverSurface must release the hook before it closes the old surface and builds the new one");
        });
        // R2-51. HostPermissionPolicy has its own native test; this proves MainForm asks it about
        // every permission request and applies the answer it gives.
        failures += Run($"MainForm answers permission requests through HostPermissionPolicy: {path}", () =>
        {
            const string core = "Microsoft.Web.WebView2.Core.CoreWebView2::";
            const string request = "Microsoft.Web.WebView2.Core.CoreWebView2PermissionRequestedEventArgs::";
            const string decide = "MatrixDesktop.HostPermissionPolicy::Decide";
            Check.True(calls.Any(call => call.Caller.StartsWith("MatrixDesktop.MainForm", StringComparison.Ordinal) && call.Callee == core + "add_PermissionRequested"),
                "MainForm must subscribe CoreWebView2.PermissionRequested");
            Check.True(calls.Any(call => call.Caller == "MatrixDesktop.MainForm::DetachWebViewHandlers" && call.Callee == core + "remove_PermissionRequested"),
                "MainForm must detach its PermissionRequested handler with the others");
            var handlers = calls.Where(call => call.Callee == decide).Select(call => call.Caller).Distinct().ToArray();
            Check.True(handlers.Length == 1 && handlers[0].StartsWith("MatrixDesktop.MainForm", StringComparison.Ordinal),
                "exactly one MainForm handler must ask HostPermissionPolicy.Decide: " + string.Join("; ", handlers));
            var body = calls.Where(call => call.Caller == handlers[0]).ToList();
            var asked = body.First(call => call.Callee == decide);
            foreach (var setter in new[] { "set_State", "set_SavesInProfile", "set_Handled" })
                Check.True(body.Any(call => call.Callee == request + setter && call.Offset > asked.Offset), $"the handler must apply the policy's answer through {setter}");
        });
        // R2-52. HostPermissionPolicy.ForgetsSavedState has its own native test; this proves each
        // WebView2 initialisation clears what it picks, to the Default state, before navigating.
        failures += Run($"MainForm clears a saved camera denial before it navigates: {path}", () =>
        {
            const string profile = "Microsoft.Web.WebView2.Core.CoreWebView2Profile::";
            const string forget = "MatrixDesktop.MainForm::ForgetSavedCameraDenialAsync";
            var init = calls.Where(call => call.Caller.StartsWith("MatrixDesktop.MainForm/<InitializeCurrentWebViewAsync>", StringComparison.Ordinal)).ToList();
            var navigate = init.FirstOrDefault(call => call.Callee == "Microsoft.Web.WebView2.Core.CoreWebView2::Navigate");
            Check.True(navigate is not null, "witness: the scan must see InitializeCurrentWebViewAsync navigate");
            var cleared = init.FirstOrDefault(call => call.Callee == forget);
            Check.True(cleared is not null && cleared.Offset < navigate!.Offset, "InitializeCurrentWebViewAsync must clear a saved camera denial before it navigates");
            var helper = calls.Where(call => call.Caller.StartsWith("MatrixDesktop.MainForm/<ForgetSavedCameraDenialAsync>", StringComparison.Ordinal)).ToList();
            var read = helper.FirstOrDefault(call => call.Callee == profile + "GetNonDefaultPermissionSettingsAsync");
            var decided = helper.FirstOrDefault(call => call.Callee == "MatrixDesktop.HostPermissionPolicy::ForgetsSavedState");
            Check.True(read is not null && decided is not null && decided.Offset > read.Offset, "the helper must read the saved settings and ask HostPermissionPolicy which to clear");
            Check.True(helper.Any(call => call.Callee == profile + "SetPermissionStateAsync" && call.PreviousOpcode == OpCodes.Ldc_I4_0.Value && call.Offset > decided!.Offset),
                "the helper must reset what the policy picks to CoreWebView2PermissionState.Default (0), which erases the saved state");
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
        failures += Run($"the tray refreshes Start with Windows when its menu opens: {path}", () =>
            Check.True(calls.Any(call => call.Caller == "MatrixDesktop.MatrixApplicationContext::CreateTray" && call.Callee == "System.Windows.Forms.ToolStripDropDown::add_Opening"),
                "CreateTray must subscribe the menu's Opening, so another launch's or the configurator's shortcut change shows"));
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
