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
        // R2-46, R2-47: the host's decisions followed through the IL, including OnSurfaceClosed's.
        failures += VerifyDecisions(path);
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
        failures += Run($"SystemEvents session and power subscriptions have one owner: {path}", () =>
        {
            foreach (var subscribe in new[] { "Microsoft.Win32.SystemEvents::add_SessionSwitch", "Microsoft.Win32.SystemEvents::add_PowerModeChanged" })
            {
                var owners = calls.Where(call => call.Callee == subscribe).Select(call => call.Caller).ToArray();
                Check.Equal("MatrixDesktop.MatrixApplicationContext::.ctor", string.Join("; ", owners),
                    $"{subscribe} must have exactly one caller, the host context, which routes it through PlaybackPolicy");
            }
        });
        // R3-13. The configurator's bridge never calls RefreshAsync, so a font install (R2-07) or a
        // Windows wallpaper change (R2-09) reaches it only through these subscriptions, and the
        // native cases call the handlers directly. Shared/NativeMediaBridge.cs is compiled into both
        // EXEs; this reads MatrixDesktop.dll's copy. Each event must be subscribed in the
        // constructor and detached in Dispose with a delegate built from its own handler.
        failures += Run($"the native media bridge follows font, preference and display changes and detaches them: {path}", () =>
        {
            const string bridge = "MatrixDesktop.Shared.NativeMediaBridge::";
            var wrong = new List<string>();
            foreach (var (name, delegateConstructor) in new[] { ("DisplaySettingsChanged", "System.EventHandler::.ctor"),
                ("UserPreferenceChanged", "Microsoft.Win32.UserPreferenceChangedEventHandler::.ctor"), ("InstalledFontsChanged", "System.EventHandler::.ctor") })
                foreach (var (method, accessor) in new[] { (".ctor", "add_"), ("Dispose", "remove_") })
                {
                    var targets = DelegateTargets(calls, functionLoads, bridge + method, delegateConstructor, "Microsoft.Win32.SystemEvents::" + accessor + name);
                    if (targets is not [var only] || only != bridge + "On" + name)
                        wrong.Add($"{method} {accessor}{name} -> [{string.Join(", ", targets)}]");
                }
            Check.True(wrong.Count == 0, "NativeMediaBridge's constructor must subscribe, and Dispose detach, each SystemEvents change with its own handler: " + string.Join("; ", wrong));
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
        // a field rather than a constant, before any teardown. That WndProc sets that field only
        // around WM_QUERYENDSESSION is R3-18's walker check. The cancelled-shutdown leg is native.
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
        });
        // R2-04. The guard's claim has its own tests; this proves the action the takeover listener
        // posts claims the request, and branches on the claim, before it ends the context. AUD-72:
        // that action is the only way a running wallpaper reacts to a newer launch, and the guard's
        // own tests pass a callback of their own, so it must end this context itself
        // (ApplicationContext.ExitThread, not Application.ExitThread). R3-20: the one owner of the
        // claimed branch; the withdrawn branch is R3-04's check in VerifyDecisions.
        failures += Run($"the wallpaper claims a takeover request before it exits: {path}", () =>
        {
            var listeners = DelegateTargets(calls, functionLoads, "MatrixDesktop.MatrixApplicationContext::TakeWallpaperOwnership",
                "System.Action::.ctor", "MatrixDesktop.WallpaperInstanceGuard::ListenForTakeover");
            Check.Equal(1, listeners.Count, "witness: the scan must resolve the takeover listener TakeWallpaperOwnership registers");
            var posted = DelegateTargets(calls, functionLoads, listeners[0], "System.Action::.ctor", "MatrixDesktop.MatrixApplicationContext/CoordinatorWindow::Post");
            Check.Equal(1, posted.Count, "the takeover listener must post one action to the UI thread: " + listeners[0]);
            var body = calls.Where(call => call.Caller == posted[0]).ToList();
            var exit = body.FirstOrDefault(call => call.Callee == ExitThread);
            var claim = body.FirstOrDefault(call => call.Callee == "MatrixDesktop.WallpaperInstanceGuard::ClaimTakeover");
            Check.True(exit is not null, "the posted takeover action must end this context with ApplicationContext.ExitThread, or every newer launch waits out its bound and quits: " + posted[0]);
            Check.True(claim is not null && claim.Offset < exit!.Offset, "the posted takeover action must claim the request before ExitThread: " + posted[0]);
            Check.True(claim!.NextOpcode is 0x2C or 0x2D or 0x39 or 0x3A, $"the posted takeover action must branch on the claim (next opcode 0x{claim.NextOpcode:X2})");
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

    // Reads, never writes: the State family that compares the shortcut with this launch (R2-03
    // removed Enabled, which only asked whether a shortcut existed).
    private static bool ReadsAutostart(string callee) => callee.StartsWith("MatrixDesktop.AutostartService::State", StringComparison.Ordinal);

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

    // R2-46, R2-47. A check that a call exists passed with the condition around it inverted or a
    // constant in place of its argument. These walk the IL of each decision with chosen inputs
    // (named field reads, isinst results and call results pinned; every other branch followed
    // both ways) and assert what the host does with each answer.
    private sealed record Instruction(int Offset, OpCode Code, string? Name, long Value, int[]? Targets, int Pops, int Pushes);

    private sealed record Step(string Name, long?[] Values, bool Store);

    private const string Context = "MatrixDesktop.MatrixApplicationContext::";
    private const string Guard = "MatrixDesktop.WallpaperInstanceGuard::";
    private const string ExitThread = "System.Windows.Forms.ApplicationContext::ExitThread";
    // The step a path ends with when its method returns a value; Values[0] is that value.
    private const string Return = "(return)";

    private static int VerifyDecisions(string path)
    {
        Dictionary<string, List<Instruction>?> bodies;
        Dictionary<string, long> actions, kinds, outcomes, autostart;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            bodies = ReadBodies(pe, reader);
            actions = EnumValues(reader, "MatrixDesktop.RendererFailureAction");
            kinds = EnumValues(reader, "MatrixDesktop.RendererFailureKind");
            outcomes = EnumValues(reader, "MatrixDesktop.WallpaperInstanceGuard/Outcome");
            autostart = EnumValues(reader, "MatrixDesktop.AutostartState");
        }
        catch (Exception error)
        {
            Console.WriteLine($"FAIL  host decision scan could not read {path}: {error.Message}");
            return 1;
        }

        var failures = 0;
        // AUD-05. The getter alone proved nothing: Decide(_wallpaper, false, ...) kept it in the IL.
        failures += Run($"OnSurfaceClosed decides through SurfaceClosePolicy with StartupFailed: {path}", () =>
        {
            foreach (var failed in new long[] { 1, 0 })
            {
                var decided = Paths(Body(bodies, Context + "OnSurfaceClosed"), new()
                {
                    ["MatrixDesktop.MainForm"] = 1, ["MatrixDesktop.MainForm::get_StartupFailed"] = failed,
                    [Context + "_exiting"] = 0, [Context + "_recovering"] = 0, [Context + "_wallpaper"] = 1, [Context + "_retryCount"] = 0,
                }).Select(steps => steps.FirstOrDefault(step => !step.Store && step.Name == "MatrixDesktop.SurfaceClosePolicy::Decide")).ToList();
                Check.True(decided.Count > 0 && decided.All(step => step is { Values: [_, long passed, _] } && passed == failed),
                    $"OnSurfaceClosed must pass the closing form's StartupFailed ({failed}) to SurfaceClosePolicy.Decide, got "
                    + string.Join(", ", decided.Select(step => step is null ? "no Decide" : "(" + string.Join(", ", step.Values.Select(value => value?.ToString() ?? "?")) + ")")));
            }
        });
        // AUD-07. MainForm's ProcessFailed handler: its kind mapping, its marshal and its guard.
        failures += Run($"the ProcessFailed handler raises RendererFailed with the same-named kind, on the UI thread, unless shutting down: {path}", () =>
        {
            var handlers = BuiltFrom(bodies, "MatrixDesktop.MainForm::_processFailedHandler");
            Check.Equal(1, handlers.Count, "witness: the scan must find the one method _processFailedHandler is built from");
            var handler = Body(bodies, handlers[0]);
            var raises = handler.Select((instruction, index) => (instruction, index)).Where(item => item.instruction.Code == OpCodes.Ldftn
                && bodies.TryGetValue(item.instruction.Name!, out var closure) && closure is not null
                && closure.Any(instruction => instruction.Code == OpCodes.Ldfld && instruction.Name == "MatrixDesktop.MainForm::RendererFailed") && closure.Any(IsInvoke)).ToList();
            Check.Equal(1, raises.Count, "the ProcessFailed handler must hand one closure that raises RendererFailed to the UI thread: " + handlers[0]);
            var next = handler.Skip(raises[0].index + 1).Where(IsCall).Take(2).Select(instruction => instruction.Name).ToArray();
            Check.True(next is ["System.Action::.ctor", "MatrixDesktop.MainForm::MarshalToUiThread"],
                "the ProcessFailed handler must raise RendererFailed through MarshalToUiThread, after WebView2's callback returns: " + string.Join(", ", next));
            var raise = Body(bodies, raises[0].instruction.Name!);
            foreach (var shuttingDown in new long[] { 0, 1 })
            {
                var paths = Paths(raise, new() { ["MatrixDesktop.MainForm::_isShuttingDown"] = shuttingDown, ["MatrixDesktop.MainForm::RendererFailed"] = 1 });
                var raised = paths.Count(steps => steps.Any(step => !step.Store && step.Name == "System.EventHandler`1::Invoke"));
                Check.True(shuttingDown == 0 ? paths.Count > 0 && raised == paths.Count : raised == 0, shuttingDown == 0
                    ? "a renderer failure must raise RendererFailed while the form runs" : "a renderer failure must not raise RendererFailed once the form is shutting down");
            }
            var argument = raise[raise.FindIndex(IsInvoke) - 1];
            Check.True(argument.Code == OpCodes.Ldfld, "witness: RendererFailed must be raised with the kind the handler captured");
            var webView = WebViewFailureKinds(path);
            Check.True(webView.ContainsKey("RenderProcessExited") && kinds.ContainsKey("RenderProcessExited") && kinds.ContainsKey("Other"),
                "witness: both kind enums must name RenderProcessExited, and RendererFailureKind Other");
            var wrong = new List<string>();
            foreach (var (name, value) in webView)
            {
                var stored = Paths(handler, new() { ["Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedEventArgs::get_ProcessFailedKind"] = value })
                    .SelectMany(steps => steps).Where(step => step.Store && step.Name == argument.Name).Select(step => step.Values[0]).Distinct().ToList();
                var expected = kinds.TryGetValue(name, out var same) ? same : kinds["Other"];
                if (stored is not [long got] || got != expected)
                    wrong.Add(name + " -> " + string.Join("/", stored.Select(got => kinds.FirstOrDefault(kind => kind.Value == got).Key ?? got?.ToString() ?? "unknown")));
            }
            Check.True(wrong.Count == 0, "each WebView2 failure kind must reach RendererFailurePolicy as the same-named kind, any other as Other: " + string.Join("; ", wrong));
        });
        // R2-51, R3-22. HostPermissionPolicy has its own native test; this walks MainForm's
        // PermissionRequested handler with the answer pinned. Decide must be asked about this
        // form's _hostKind; an unhandled answer must leave the request to WebView2's prompt; a
        // handled one must apply the answer's State and SavesInProfile, not constants. Handled is
        // not required: on a CoreWebView2-level handler it decides nothing.
        failures += Run($"MainForm answers permission requests through HostPermissionPolicy: {path}", () =>
        {
            const string core = "Microsoft.Web.WebView2.Core.CoreWebView2::";
            const string request = "Microsoft.Web.WebView2.Core.CoreWebView2PermissionRequestedEventArgs::";
            const string answer = "MatrixDesktop.HostPermissionPolicy/Answer::";
            const string decide = "MatrixDesktop.HostPermissionPolicy::Decide";
            bool Calls(string caller, string callee) => bodies.GetValueOrDefault(caller)?.Any(instruction => IsCall(instruction) && instruction.Name == callee) == true;
            Check.True(bodies.Keys.Any(caller => caller.StartsWith("MatrixDesktop.MainForm", StringComparison.Ordinal) && Calls(caller, core + "add_PermissionRequested")),
                "MainForm must subscribe CoreWebView2.PermissionRequested");
            Check.True(Calls("MatrixDesktop.MainForm::DetachWebViewHandlers", core + "remove_PermissionRequested"), "MainForm must detach its PermissionRequested handler with the others");
            var handlers = BuiltFrom(bodies, "MatrixDesktop.MainForm::_permissionRequestedHandler");
            Check.Equal(1, handlers.Count, "witness: the scan must find the one method _permissionRequestedHandler is built from");
            var handler = Body(bodies, handlers[0]);
            // Sentinels no constant in the handler could stand in for.
            const long host = 77, state = 78, saves = 79;
            var asked = Paths(handler, new() { ["MatrixDesktop.MainForm::_hostKind"] = host })
                .Select(steps => steps.FirstOrDefault(step => !step.Store && step.Name == decide)).ToList();
            Check.True(asked.Count > 0 && asked.All(step => step is { Values: [long kind, _] } && kind == host),
                $"the handler must ask HostPermissionPolicy.Decide about this form's _hostKind ({host}), got "
                + string.Join(", ", asked.Select(step => step is null ? "no Decide" : step.Values[0]?.ToString() ?? "?")));
            string Applied(List<Step> steps) => string.Join(" ", steps.Where(step => !step.Store && step.Name is request + "set_State" or request + "set_SavesInProfile")
                .Select(step => step.Name[request.Length..] + "=" + (step.Values[1]?.ToString() ?? "?")));
            var unhandled = Paths(handler, new() { [answer + "get_Handled"] = 0 }).Select(Applied).Distinct().ToList();
            Check.True(unhandled is [""], "an answer that is not Handled must leave the request to WebView2's prompt, got '" + string.Join("' or '", unhandled) + "'");
            var handled = Paths(handler, new() { [answer + "get_Handled"] = 1, [answer + "get_State"] = state, [answer + "get_SavesInProfile"] = saves })
                .Select(Applied).Distinct().ToList();
            Check.True(handled is [var only] && only == $"set_State={state} set_SavesInProfile={saves}",
                $"a Handled answer must set the request's State and SavesInProfile to the answer's ({state}, {saves}), got '" + string.Join("' or '", handled) + "'");
        });
        // R2-52, R3-23. HostPermissionPolicy.ForgetsSavedState has its own native test; this proves
        // each WebView2 initialisation awaits, before it navigates, a helper that resets to the
        // Default state only the settings the policy picks. A discarded task let Navigate race the
        // reset; an unconditional reset erased a saved camera Allow and every other origin's state.
        failures += Run($"MainForm clears a saved camera denial before it navigates: {path}", () =>
        {
            const string profile = "Microsoft.Web.WebView2.Core.CoreWebView2Profile::";
            static int Find(List<Instruction> code, string callee, int from = 0) => code.FindIndex(from, instruction => IsCall(instruction) && instruction.Name == callee);
            var init = Body(bodies, StateMachine(bodies, "MatrixDesktop.MainForm/<InitializeCurrentWebViewAsync>"));
            var navigate = Find(init, "Microsoft.Web.WebView2.Core.CoreWebView2::Navigate");
            Check.True(navigate >= 0, "witness: the scan must see InitializeCurrentWebViewAsync navigate");
            var cleared = Find(init, "MatrixDesktop.MainForm::ForgetSavedCameraDenialAsync");
            Check.True(cleared >= 0 && cleared < navigate, "InitializeCurrentWebViewAsync must clear a saved camera denial before it navigates");
            var awaited = init[cleared + 1];
            Check.True(IsCall(awaited) && awaited.Name == "System.Threading.Tasks.Task::GetAwaiter",
                $"InitializeCurrentWebViewAsync must await the clear, not discard its task (next {awaited.Code.Name} {awaited.Name})");
            var result = Find(init, "System.Runtime.CompilerServices.TaskAwaiter::GetResult", cleared);
            Check.True(result >= 0 && result < navigate, "the clear's await must complete before InitializeCurrentWebViewAsync navigates");
            var helper = Body(bodies, StateMachine(bodies, "MatrixDesktop.MainForm/<ForgetSavedCameraDenialAsync>"));
            var read = Find(helper, profile + "GetNonDefaultPermissionSettingsAsync");
            var decided = Find(helper, "MatrixDesktop.HostPermissionPolicy::ForgetsSavedState");
            Check.True(read >= 0 && decided > read, "the helper must read the saved settings and ask HostPermissionPolicy which to clear");
            var reset = Find(helper, profile + "SetPermissionStateAsync", decided + 1);
            // Every reset the helper makes, not only the first after the question: one added before
            // the question, before the loop or on its false edge reset what the policy keeps.
            var resets = Enumerable.Range(0, helper.Count).Where(index => IsCall(helper[index]) && helper[index].Name == profile + "SetPermissionStateAsync").ToList();
            Check.True(reset > decided && resets.All(index => helper[index - 1].Code == OpCodes.Ldc_I4_0),
                "the helper must reset what the policy picks to CoreWebView2PermissionState.Default (0), which erases the saved state");
            var branch = helper[decided + 1];
            Check.True(branch.Code.Name!.StartsWith("brtrue", StringComparison.Ordinal) || branch.Code.Name.StartsWith("brfalse", StringComparison.Ordinal),
                $"the helper must branch on ForgetsSavedState's answer (next {branch.Code.Name})");
            var target = helper.FindIndex(instruction => instruction.Offset == branch.Value);
            var (whenTrue, whenFalse) = branch.Code.Name.StartsWith("brtrue", StringComparison.Ordinal) ? (target, decided + 2) : (decided + 2, target);
            var ungated = resets.Where(index => Flows(helper, 0, index, decided) || Flows(helper, whenFalse, index, decided))
                .Select(index => $"IL_{helper[index].Offset:X4}").ToList();
            Check.True(Flows(helper, whenTrue, reset, decided) && ungated.Count == 0,
                "the helper must reset a saved setting only when ForgetsSavedState answers true, and keep it (a saved Allow, another origin's state) otherwise"
                + (ungated.Count > 0 ? "; reached without its true answer: " + string.Join(", ", ungated) : ""));
        });
        // R2-01, R3-18. OnFormClosing hands HostClosePolicy _sessionEndQuery; WndProc must set it
        // to true for WM_QUERYENDSESSION (0x11) alone, before the base dispatch, and reset it in a
        // finally around that dispatch. The old leg only found a base.WndProc call, which both
        // branches make, so it passed with the store deleted.
        failures += Run($"MainForm sets the session-end flag HostClosePolicy reads only while WM_QUERYENDSESSION is dispatched: {path}", () =>
        {
            const string flag = "MatrixDesktop.MainForm::_sessionEndQuery";
            const string dispatch = "System.Windows.Forms.Form::WndProc";
            var code = Body(bodies, "MatrixDesktop.MainForm::WndProc");
            string Seen(List<Step> steps) => string.Join(" ", steps.Where(step => step.Store ? step.Name == flag : step.Name == dispatch)
                .Select(step => step.Store ? "flag=" + (step.Values[0]?.ToString() ?? "?") : "dispatch"));
            foreach (var message in new long[] { 0x11, 0x10 })
            {
                var expected = message == 0x11 ? "flag=1 dispatch" : "dispatch";
                var seen = Paths(code, new() { ["System.Windows.Forms.Message::get_Msg"] = message }).Select(Seen).Distinct().ToList();
                Check.True(seen is [var only] && only == expected, $"WndProc must turn message 0x{message:X2} into '{expected}', got '{string.Join("' or '", seen)}'");
            }
            // The finally must cover the dispatch that follows the store of true, not any dispatch:
            // one around the other branch's left the flag set after the query.
            var sets = Enumerable.Range(1, code.Count - 1).Where(index => code[index].Code == OpCodes.Stfld && code[index].Name == flag && code[index - 1].Code == OpCodes.Ldc_I4_1).ToList();
            var query = sets is [var set] ? code.FindIndex(set, instruction => IsCall(instruction) && instruction.Name == dispatch) : -1;
            Check.True(query >= 0, $"witness: WndProc must store true in the flag once ({sets.Count} stores) and dispatch after it");
            var guarded = FinallyRegions(path, "MatrixDesktop.MainForm::WndProc")
                .Where(region => code[query].Offset >= region.TryOffset && code[query].Offset < region.TryOffset + region.TryLength).ToList();
            Check.Equal(1, guarded.Count, $"WndProc must dispatch WM_QUERYENDSESSION (the call at IL_{code[query].Offset:X4}, after the flag's store of true) inside one try/finally");
            var handler = code.Where(instruction => instruction.Offset >= guarded[0].HandlerOffset && instruction.Offset < guarded[0].HandlerOffset + guarded[0].HandlerLength).ToList();
            var reset = Paths(handler, new()).Select(Seen).Distinct().ToList();
            Check.True(reset is ["flag=0"], "the finally around the dispatch must reset the flag to false, got '" + string.Join("' or '", reset) + "'");
            const long sentinel = 7;
            var read = Paths(Body(bodies, "MatrixDesktop.MainForm::OnFormClosing"), new() { [flag] = sentinel })
                .Select(steps => steps.FirstOrDefault(step => !step.Store && step.Name == "MatrixDesktop.HostClosePolicy::TearsDownOnClosing")).ToList();
            Check.True(read.Count > 0 && read.All(step => step is { Values: [long passed] } && passed == sentinel),
                $"OnFormClosing must hand HostClosePolicy the flag WndProc sets ({sentinel}), got "
                + string.Join(", ", read.Select(step => step is null ? "no call" : step.Values[0]?.ToString() ?? "?")));
        });
        failures += Run($"OnRendererFailed reloads, rebuilds or exits as RendererFailurePolicy decides: {path}", () =>
        {
            Check.True(actions.Keys.Order().SequenceEqual(["Exit", "LogOnly", "Recreate", "Reload"]),
                "witness: RendererFailureAction must be LogOnly, Reload, Recreate and Exit: " + string.Join(", ", actions.Keys));
            var wrong = new List<string>();
            foreach (var (action, value) in actions)
                foreach (var wallpaper in new long[] { 1, 0 })
                    foreach (var reloads in new long[] { 1, 0 })
                    {
                        var expected = (action == "Reload" ? "reload " : "") + (action, wallpaper, reloads) switch
                        {
                            ("LogOnly", _, _) or ("Reload", _, 1) => "keep",
                            ("Reload" or "Recreate", 1, _) => "close rebuild",
                            _ => "close exit",
                        };
                        var seen = Paths(Body(bodies, Context + "OnRendererFailed"), new()
                        {
                            // The failed surface is the current one: isinst and _form give the same reference.
                            [Context + "_exiting"] = 0, ["MatrixDesktop.MainForm"] = 1, [Context + "_form"] = 1,
                            ["MatrixDesktop.RendererFailurePolicy::Decide"] = value, [Context + "_wallpaper"] = wallpaper, ["MatrixDesktop.MainForm::ReloadRenderer"] = reloads,
                        }).Select(Outcome).Distinct().ToList();
                        if (seen is not [var only] || only != expected)
                            wrong.Add($"{action} wallpaper={wallpaper} reloaded={reloads}: expected '{expected}', got '{string.Join("' or '", seen)}'");
                    }
            Check.True(wrong.Count == 0, "the context must reload only on Reload and keep a reloaded surface; otherwise close it, then rebuild a wallpaper or end any other host: "
                + string.Join("; ", wrong));
        });
        failures += Run($"the wallpaper tick resets the rebuild backoff once a renderer stayed ready: {path}", () =>
        {
            foreach (var healthy in new long[] { 1, 0 })
            {
                var stored = Paths(Body(bodies, Context + "OnTick"), new()
                {
                    [Context + "_exiting"] = 0, ["MatrixDesktop.HostLaunchPlan::get_IsScreensaver"] = 0, ["MatrixDesktop.HostLaunchPlan::get_IsPreview"] = 0,
                    [Context + "_form"] = 1, ["MatrixDesktop.DesktopWindowHost::IsUsable"] = 1, ["MatrixDesktop.DesktopWindowHost::ParentOf"] = 7, [Context + "_parent"] = 7,
                    [Context + "_retryCount"] = 3, ["MatrixDesktop.RendererFailurePolicy::StayedHealthy"] = healthy,
                }).Select(steps => string.Join("/", steps.Where(step => step.Store && step.Name == Context + "_retryCount").Select(step => step.Values[0]))).Distinct().ToList();
                Check.True(healthy == 1 ? stored is ["0"] : stored is [""], (healthy == 1 ? "a renderer that stayed ready must reset the rebuild backoff to 0"
                    : "a renderer that has not stayed ready must keep the rebuild backoff") + ", got stores '" + string.Join("' or '", stored) + "'");
            }
        });
        // R3-04. TakeWallpaperOwnership walked with the guard's Result pinned to each Outcome the
        // enum defines. A launch that timed out or was superseded must end (ExitThread, false) and
        // never listen: a superseded one that went on built a second tray and surface with no scope
        // mutex (AUD-72). One that acquired or took over must listen for a newer launch; one whose
        // guard is unavailable runs unguarded.
        failures += Run($"TakeWallpaperOwnership ends a launch that did not take ownership and listens on one that did: {path}", () =>
        {
            Check.True(outcomes.Keys.Order().SequenceEqual(["Acquired", "Superseded", "TimedOut", "TookOver", "Unavailable"]),
                "witness: WallpaperInstanceGuard.Outcome must be Acquired, TookOver, TimedOut, Unavailable and Superseded: " + string.Join(", ", outcomes.Keys));
            var wrong = new List<string>();
            foreach (var (outcome, value) in outcomes)
            {
                var expected = outcome switch { "TimedOut" or "Superseded" => "exit return 0", "Unavailable" => "return 1", _ => "listen return 1" };
                var seen = Paths(Body(bodies, Context + "TakeWallpaperOwnership"), new() { [Guard + "get_Result"] = value }).Select(steps => string.Join(" ", new[]
                {
                    steps.Any(step => !step.Store && step.Name == ExitThread) ? "exit" : null,
                    steps.Any(step => !step.Store && step.Name == Guard + "ListenForTakeover") ? "listen" : null,
                    "return " + (steps.LastOrDefault(step => step.Name == Return)?.Values[0]?.ToString() ?? "?"),
                }.OfType<string>())).Distinct().ToList();
                if (seen is not [var only] || only != expected)
                    wrong.Add($"{outcome}: expected '{expected}', got '{string.Join("' or '", seen)}'");
            }
            Check.True(wrong.Count == 0, "TakeWallpaperOwnership must end a timed-out or superseded launch without listening, and listen on one that took ownership: "
                + string.Join("; ", wrong));
        });
        // R2-03, R3-19. The tray's ShowAutostart walked with AutostartService.State pinned to each
        // AutostartState: only ThisLaunch ticks Start with Windows. A shortcut that starts other
        // settings shows unticked, so one click stores this launch's (ClickEnables).
        failures += Run($"the tray ticks Start with Windows only when the shortcut starts this launch: {path}", () =>
        {
            Check.True(autostart.Keys.Order().SequenceEqual(["Off", "OtherSettings", "ThisLaunch"]),
                "witness: AutostartState must be Off, ThisLaunch and OtherSettings: " + string.Join(", ", autostart.Keys));
            var show = bodies.Keys.Where(key => key.StartsWith("MatrixDesktop.MatrixApplicationContext", StringComparison.Ordinal)
                && key.Contains("::<CreateTray>g__ShowAutostart|", StringComparison.Ordinal)).ToList();
            Check.Equal(1, show.Count, "witness: the scan must find CreateTray's ShowAutostart");
            var wrong = new List<string>();
            foreach (var (state, value) in autostart)
            {
                var expected = state == "ThisLaunch" ? "1" : "0";
                var ticked = Paths(Body(bodies, show[0]), new() { ["MatrixDesktop.AutostartService::State"] = value }).Select(steps => string.Join("/",
                    steps.Where(step => !step.Store && step.Name == "System.Windows.Forms.ToolStripMenuItem::set_Checked").Select(step => step.Values[1]?.ToString() ?? "?"))).Distinct().ToList();
                if (ticked is not [var only] || only != expected)
                    wrong.Add($"{state}: expected Checked={expected}, got '{string.Join("' or '", ticked)}'");
            }
            Check.True(wrong.Count == 0, "Start with Windows must be ticked exactly when AutostartService.State is ThisLaunch: " + string.Join("; ", wrong));
        });
        // R3-04. ListenForTakeover fires once, and a false claim (the newcomer withdrew at its
        // bound) is reachable after every timed-out newcomer, so the withdrawn branch must listen
        // again with a callback that posts OnTakeoverRequested; without it every later launch waits
        // out its bound. The claimed branch is the R2-04 check's.
        failures += Run($"a withdrawn takeover request keeps this wallpaper listening: {path}", () =>
        {
            var code = Body(bodies, Context + "OnTakeoverRequested");
            var paths = Paths(code, new() { [Context + "_exiting"] = 0, [Context + "_instance"] = 1, [Guard + "ClaimTakeover"] = 0 });
            var listened = paths.Count(steps => steps.Any(step => !step.Store && step.Name == Guard + "ListenForTakeover"));
            var exited = paths.Count(steps => steps.Any(step => !step.Store && step.Name == ExitThread));
            Check.True(paths.Count > 0 && listened == paths.Count && exited == 0,
                $"a withdrawn takeover request must listen again and keep this wallpaper: {paths.Count} paths, {listened} listen, {exited} exit");
            var listen = code.FindIndex(instruction => IsCall(instruction) && instruction.Name == Guard + "ListenForTakeover");
            var callback = code.Take(listen).LastOrDefault(instruction => instruction.Code == OpCodes.Ldftn)?.Name;
            var posts = callback is null || !bodies.TryGetValue(callback, out var built) || built is null ? [] : built;
            Check.True(posts.Any(instruction => instruction.Code == OpCodes.Ldftn && instruction.Name == Context + "OnTakeoverRequested")
                && posts.Any(instruction => IsCall(instruction) && instruction.Name == "MatrixDesktop.MatrixApplicationContext/CoordinatorWindow::Post"),
                "the renewed listener must post OnTakeoverRequested to the UI thread again: " + callback);
        });
        failures += Run($"a wallpaper launch that did not take ownership stops before its tray: {path}", () =>
        {
            foreach (var took in new long[] { 1, 0 })
            {
                var paths = Paths(Body(bodies, Context + ".ctor"), new() { [Context + "TakeWallpaperOwnership"] = took, [Context + "_wallpaper"] = 1 });
                var built = paths.Count(steps => steps.Any(step => !step.Store && step.Name is Context + "CreateTray" or Context + "TryCreateSurface"));
                Check.True(took == 1 ? paths.Count > 0 && built == paths.Count : built == 0, took == 1
                    ? "witness: a wallpaper launch that took ownership must create its tray and surface"
                    : "a wallpaper launch that did not take ownership has already ended, so it must not create a tray or a surface");
            }
        });
        return failures;
    }

    private static bool IsCall(Instruction instruction) => instruction.Code == OpCodes.Call || instruction.Code == OpCodes.Callvirt || instruction.Code == OpCodes.Newobj;

    private static bool IsInvoke(Instruction instruction) => IsCall(instruction) && instruction.Name == "System.EventHandler`1::Invoke";

    // What one path did: a reload, a close, an exit and a rebuild (the retry time it set), else keep.
    private static string Outcome(List<Step> steps)
    {
        var parts = new List<string>();
        if (steps.Any(step => !step.Store && step.Name == "MatrixDesktop.MainForm::ReloadRenderer")) parts.Add("reload");
        if (steps.Any(step => !step.Store && step.Name == FormClose)) parts.Add("close");
        if (steps.Any(step => !step.Store && step.Name == ExitThread)) parts.Add("exit");
        if (steps.Any(step => step.Store && step.Name == Context + "_retryAt")) parts.Add("rebuild");
        if (!parts.Any(part => part is "close" or "exit" or "rebuild")) parts.Add("keep");
        return string.Join(" ", parts);
    }

    // The method a delegate field is built from: the nearest ldftn before each store of it. A
    // lambda that captures nothing is cached first, in MainForm/<>c.
    private static List<string> BuiltFrom(Dictionary<string, List<Instruction>?> bodies, string field)
    {
        var built = new List<string>();
        foreach (var code in bodies.Values.OfType<List<Instruction>>())
            for (var index = 1; index < code.Count; index++)
                if (code[index].Code == OpCodes.Stfld && code[index].Name == field
                    && code.Take(index).TakeLast(8).LastOrDefault(instruction => instruction.Code == OpCodes.Ldftn) is { Name: string method })
                    built.Add(method);
        return built;
    }

    // The try/finally regions of the one body named method ("Type::Method"), as IL offsets.
    private static List<ExceptionRegion> FinallyRegions(string path, string method)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var found = reader.TypeDefinitions.Select(reader.GetTypeDefinition).SelectMany(type => type.GetMethods().Select(reader.GetMethodDefinition)
            .Where(definition => definition.RelativeVirtualAddress != 0 && TypeName(reader, type) + "::" + reader.GetString(definition.Name) == method)).ToList();
        Check.Equal(1, found.Count, "witness: the scan must find one " + method);
        return pe.GetMethodBody(found[0].RelativeVirtualAddress).ExceptionRegions.Where(region => region.Kind == ExceptionRegionKind.Finally).ToList();
    }

    // The MoveNext of the one async state machine whose type name starts with prefix.
    private static string StateMachine(Dictionary<string, List<Instruction>?> bodies, string prefix)
    {
        var found = bodies.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && key.EndsWith("::MoveNext", StringComparison.Ordinal)).ToList();
        Check.Equal(1, found.Count, "witness: the scan must find one state machine " + prefix);
        return found[0];
    }

    // True when control can flow from index from to index to without passing index stop, every
    // branch followed both ways; exception handlers are not entered.
    private static bool Flows(List<Instruction> code, int from, int to, int stop)
    {
        var at = code.Select((instruction, index) => (instruction.Offset, index)).ToDictionary(pair => pair.Offset, pair => pair.index);
        var seen = new HashSet<int>();
        var work = new Stack<int>([from]);
        while (work.Count > 0)
        {
            var index = work.Pop();
            if (index < 0 || index == stop || !seen.Add(index)) continue;
            if (index == to) return true;
            var op = code[index].Code;
            if (op == OpCodes.Ret || op == OpCodes.Throw || op == OpCodes.Rethrow || op == OpCodes.Endfinally) continue;
            if (op == OpCodes.Switch) foreach (var target in code[index].Targets!) work.Push(at[target]);
            if (op.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch) work.Push(at[(int)code[index].Value]);
            if (op.FlowControl != FlowControl.Branch) work.Push(index + 1);
        }
        return false;
    }

    private static List<Instruction> Body(Dictionary<string, List<Instruction>?> bodies, string method) =>
        bodies.TryGetValue(method, out var code) ? code ?? throw new InvalidOperationException("the scan found more than one " + method)
            : throw new InvalidOperationException("the scan found no " + method);

    private static Dictionary<string, long> EnumValues(MetadataReader reader, string typeName)
    {
        var types = reader.TypeDefinitions.Select(reader.GetTypeDefinition).Where(type => TypeName(reader, type) == typeName).ToList();
        Check.Equal(1, types.Count, "witness: the scan must find " + typeName);
        return types[0].GetFields().Select(reader.GetFieldDefinition).Where(field => !field.GetDefaultValue().IsNil)
            .ToDictionary(field => reader.GetString(field.Name), field => (long)reader.GetBlobReader(reader.GetConstant(field.GetDefaultValue()).Value).ReadInt32());
    }

    // The WebView2 core assembly is published beside MatrixDesktop.dll; a missing one fails the check.
    private static Dictionary<string, long> WebViewFailureKinds(string path)
    {
        using var stream = File.OpenRead(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "Microsoft.Web.WebView2.Core.dll"));
        using var pe = new PEReader(stream);
        return EnumValues(pe.GetMetadataReader(), "Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind");
    }

    // Every path through code from its first instruction. Each path lists the calls, with the
    // argument values the walk could tell, and the field stores along it.
    private static List<List<Step>> Paths(List<Instruction> code, Dictionary<string, long> facts)
    {
        var at = new Dictionary<int, int>();
        for (var index = 0; index < code.Count; index++) at[code[index].Offset] = index;
        var done = new List<List<Step>>();
        var work = new Stack<(int Index, List<long?> Stack, Dictionary<long, long?> Locals, List<Step> Steps)>();
        work.Push((0, new List<long?>(), new Dictionary<long, long?>(), new List<Step>()));
        var budget = 200_000;
        while (work.Count > 0)
        {
            var (index, stack, locals, steps) = work.Pop();
            while (true)
            {
                if (--budget < 0) throw new InvalidOperationException("the IL walk did not finish");
                var instruction = code[index];
                var op = instruction.Code;
                var name = op.Name!;
                long? Pop() { var top = stack[^1]; stack.RemoveAt(stack.Count - 1); return top; }
                long? Fact() => instruction.Name is not null && facts.TryGetValue(instruction.Name, out var known) ? known : null;
                void Fork(int target) => work.Push((at[target], new List<long?>(stack), new Dictionary<long, long?>(locals), new List<Step>(steps)));
                if (op == OpCodes.Ret && stack.Count > 0) steps.Add(new Step(Return, [stack[^1]], Store: false));
                if (op == OpCodes.Ret || op == OpCodes.Throw || op == OpCodes.Rethrow || op == OpCodes.Endfinally) { done.Add(steps); break; }
                if (op == OpCodes.Switch)
                {
                    var selector = Pop();
                    if (selector is null) { foreach (var target in instruction.Targets!) Fork(target); index++; }
                    else index = selector >= 0 && selector < instruction.Targets!.Length ? at[instruction.Targets[(int)selector]] : index + 1;
                    continue;
                }
                if (op.FlowControl == FlowControl.Branch) { index = at[(int)instruction.Value]; continue; }
                if (op.FlowControl == FlowControl.Cond_Branch)
                {
                    bool? taken;
                    if (name.StartsWith("brtrue", StringComparison.Ordinal)) taken = Pop() is long truth ? truth != 0 : null;
                    else if (name.StartsWith("brfalse", StringComparison.Ordinal)) taken = Pop() is long truth ? truth == 0 : null;
                    else
                    {
                        var right = Pop();
                        var left = Pop();
                        taken = left is long a && right is long b ? name.Split('.')[0] switch
                        {
                            "beq" => a == b, "bne" => a != b, "bge" => a >= b, "bgt" => a > b, "ble" => a <= b, "blt" => a < b,
                            _ => throw new InvalidOperationException("the IL walk cannot follow " + name),
                        } : null;
                    }
                    if (taken is null) { Fork((int)instruction.Value); index++; }
                    else index = taken.Value ? at[(int)instruction.Value] : index + 1;
                    continue;
                }
                if (name.StartsWith("ldc.i", StringComparison.Ordinal)) stack.Add(instruction.Value);
                else if (op == OpCodes.Ldnull) stack.Add(0);
                else if (name.StartsWith("ldloc", StringComparison.Ordinal) && !name.StartsWith("ldloca", StringComparison.Ordinal)) stack.Add(locals.GetValueOrDefault(instruction.Value));
                else if (name.StartsWith("stloc", StringComparison.Ordinal)) locals[instruction.Value] = Pop();
                else if (op == OpCodes.Dup) { var top = Pop(); stack.Add(top); stack.Add(top); }
                else if (op == OpCodes.Ldfld || op == OpCodes.Isinst) { Pop(); stack.Add(Fact()); }
                else if (op == OpCodes.Ldsfld) stack.Add(Fact());
                else if (op == OpCodes.Stfld || op == OpCodes.Stsfld)
                {
                    var stored = Pop();
                    if (op == OpCodes.Stfld) Pop();
                    steps.Add(new Step(instruction.Name!, [stored], Store: true));
                }
                else if (IsCall(instruction))
                {
                    var values = new long?[instruction.Pops];
                    for (var slot = values.Length - 1; slot >= 0; slot--) values[slot] = Pop();
                    steps.Add(new Step(instruction.Name!, values, Store: false));
                    if (instruction.Pushes > 0) stack.Add(op == OpCodes.Newobj ? null : Fact());
                }
                else if (name is "ceq" or "cgt" or "cgt.un" or "clt" or "clt.un")
                {
                    var right = Pop();
                    var left = Pop();
                    stack.Add(left is long a && right is long b ? ((name[1..3] switch { "eq" => a == b, "gt" => a > b, _ => a < b }) ? 1 : 0) : null);
                }
                else if (name.StartsWith("conv.", StringComparison.Ordinal)) stack.Add(Pop());
                else
                {
                    for (var count = PopCount(op.StackBehaviourPop); count > 0; count--) Pop();
                    for (var count = op.StackBehaviourPush == StackBehaviour.Push0 ? 0 : op.StackBehaviourPush == StackBehaviour.Push1_push1 ? 2 : 1; count > 0; count--) stack.Add(null);
                }
                index++;
            }
        }
        return done;
    }

    private static int PopCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4
            or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
            or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref or StackBehaviour.Popref_popi_pop1 => 3,
        _ => throw new InvalidOperationException("the IL walk cannot follow stack behaviour " + behaviour),
    };

    // Each method body as instructions, keyed "Type::Method"; a name two bodies share maps to null.
    private static Dictionary<string, List<Instruction>?> ReadBodies(PEReader pe, MetadataReader reader)
    {
        var bodies = new Dictionary<string, List<Instruction>?>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
                var code = new List<Instruction>();
                while (il.RemainingBytes > 0)
                {
                    var offset = il.Offset;
                    int value = il.ReadByte();
                    if (value == 0xFE) value = 0xFE00 | il.ReadByte();
                    var op = OpCodesByValue[unchecked((short)value)];
                    string? name = null;
                    int[]? targets = null;
                    var (pops, pushes) = (0, 0);
                    // ldarg.0-3, ldloc.0-3, stloc.0-3 and ldc.i4.m1-8 carry their operand in the opcode.
                    long operand = op.Value switch { >= 0x02 and <= 0x05 => op.Value - 0x02, >= 0x06 and <= 0x09 => op.Value - 0x06,
                        >= 0x0A and <= 0x0D => op.Value - 0x0A, >= 0x15 and <= 0x1E => op.Value - 0x16, _ => 0 };
                    switch (op.OperandType)
                    {
                        case OperandType.InlineNone: break;
                        case OperandType.ShortInlineBrTarget: operand = il.ReadSByte(); operand += il.Offset; break;
                        case OperandType.InlineBrTarget: operand = il.ReadInt32(); operand += il.Offset; break;
                        case OperandType.ShortInlineI: operand = op == OpCodes.Ldc_I4_S ? il.ReadSByte() : il.ReadByte(); break;
                        case OperandType.ShortInlineVar: operand = il.ReadByte(); break;
                        case OperandType.InlineVar: operand = il.ReadUInt16(); break;
                        case OperandType.InlineI: operand = il.ReadInt32(); break;
                        case OperandType.InlineI8: operand = il.ReadInt64(); break;
                        case OperandType.ShortInlineR: il.ReadSingle(); break;
                        case OperandType.InlineR: il.ReadDouble(); break;
                        case OperandType.InlineSwitch:
                            targets = new int[il.ReadInt32()];
                            for (var slot = 0; slot < targets.Length; slot++) targets[slot] = il.ReadInt32();
                            for (var slot = 0; slot < targets.Length; slot++) targets[slot] += il.Offset;
                            break;
                        case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok:
                            var token = MetadataTokens.EntityHandle(il.ReadInt32());
                            name = TokenName(reader, token);
                            if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj) (pops, pushes) = CallShape(reader, token, op == OpCodes.Newobj);
                            break;
                        default: il.ReadInt32(); break;
                    }
                    code.Add(new Instruction(offset, op, name, operand, targets, pops, pushes));
                }
                var key = TypeName(reader, type) + "::" + reader.GetString(method.Name);
                bodies[key] = bodies.ContainsKey(key) ? null : code;
            }
        }
        return bodies;
    }

    // What a call takes off the stack (this included) and leaves on it, from its signature.
    private static (int Pops, int Pushes) CallShape(MetadataReader reader, EntityHandle handle, bool construct)
    {
        if (handle.Kind == HandleKind.MethodSpecification) handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        var blob = reader.GetBlobReader(handle.Kind == HandleKind.MethodDefinition
            ? reader.GetMethodDefinition((MethodDefinitionHandle)handle).Signature : reader.GetMemberReference((MemberReferenceHandle)handle).Signature);
        var header = blob.ReadSignatureHeader();
        if (header.IsGeneric) blob.ReadCompressedInteger();
        var count = blob.ReadCompressedInteger();
        var returns = blob.ReadSignatureTypeCode();
        while (returns is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier) { blob.ReadTypeHandle(); returns = blob.ReadSignatureTypeCode(); }
        return construct ? (count, 1) : (count + (header.IsInstance && !header.HasExplicitThis ? 1 : 0), returns == SignatureTypeCode.Void ? 0 : 1);
    }

    // Like MethodName, for fields and types too, naming a generic instance by its definition.
    private static string TokenName(MetadataReader reader, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.FieldDefinition:
                var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                return TypeName(reader, reader.GetTypeDefinition(field.GetDeclaringType())) + "::" + reader.GetString(field.Name);
            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return TypeName(reader, reader.GetTypeDefinition(method.GetDeclaringType())) + "::" + reader.GetString(method.Name);
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                return TokenName(reader, member.Parent) + "::" + reader.GetString(member.Name);
            case HandleKind.MethodSpecification:
                return TokenName(reader, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
            case HandleKind.TypeDefinition:
                return TypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle));
            case HandleKind.TypeReference:
                return TypeReferenceName(reader, reader.GetTypeReference((TypeReferenceHandle)handle));
            case HandleKind.TypeSpecification:
                var blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return "TypeSpecification";
                blob.ReadSignatureTypeCode();
                return TokenName(reader, blob.ReadTypeHandle());
            default:
                return handle.Kind.ToString();
        }
    }
}
