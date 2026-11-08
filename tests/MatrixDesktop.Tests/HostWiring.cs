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
        // R2-46, R2-47: the host's decisions followed through the IL, including OnSurfaceClosed's.
        failures += VerifyDecisions(path);
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

    // R2-46, R2-47. A check that a call exists passed with the condition around it inverted or a
    // constant in place of its argument. These walk the IL of each decision with chosen inputs
    // (named field reads, isinst results and call results pinned; every other branch followed
    // both ways) and assert what the host does with each answer.
    private sealed record Instruction(int Offset, OpCode Code, string? Name, long Value, int[]? Targets, int Pops, int Pushes);

    private sealed record Step(string Name, long?[] Values, bool Store);

    private const string Context = "MatrixDesktop.MatrixApplicationContext::";
    private const string ExitThread = "System.Windows.Forms.ApplicationContext::ExitThread";

    private static int VerifyDecisions(string path)
    {
        Dictionary<string, List<Instruction>?> bodies;
        Dictionary<string, long> actions, kinds;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            bodies = ReadBodies(pe, reader);
            actions = EnumValues(reader, "MatrixDesktop.RendererFailureAction");
            kinds = EnumValues(reader, "MatrixDesktop.RendererFailureKind");
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
            // The nearest ldftn before the store; a lambda that captures nothing is cached first.
            var handlers = new List<string>();
            foreach (var code in bodies.Values.OfType<List<Instruction>>())
                for (var index = 1; index < code.Count; index++)
                    if (code[index].Code == OpCodes.Stfld && code[index].Name == "MatrixDesktop.MainForm::_processFailedHandler"
                        && code.Take(index).TakeLast(8).LastOrDefault(instruction => instruction.Code == OpCodes.Ldftn) is { Name: string built })
                        handlers.Add(built);
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
        // AUD-72. ListenForTakeover's callback is the only way a running wallpaper reacts to a
        // newer launch, and the guard's own tests pass a callback of their own.
        failures += Run($"the takeover callback ends this wallpaper: {path}", () =>
        {
            var code = Body(bodies, Context + "TakeWallpaperOwnership");
            var listen = code.FindIndex(instruction => IsCall(instruction) && instruction.Name == "MatrixDesktop.WallpaperInstanceGuard::ListenForTakeover");
            var callback = code.Take(Math.Max(listen, 0)).LastOrDefault(instruction => instruction.Code == OpCodes.Ldftn)?.Name;
            Check.True(callback is not null, "witness: the scan must see the callback TakeWallpaperOwnership hands to ListenForTakeover");
            Check.True(Leads(bodies, callback!, ExitThread, depth: 3), "the takeover callback must end this wallpaper, or every newer launch waits out its bound and quits: " + callback);
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

    private static List<Instruction> Body(Dictionary<string, List<Instruction>?> bodies, string method) =>
        bodies.TryGetValue(method, out var code) ? code ?? throw new InvalidOperationException("the scan found more than one " + method)
            : throw new InvalidOperationException("the scan found no " + method);

    // True when method, a closure it builds or a MatrixDesktop method it calls, within depth steps, calls callee.
    private static bool Leads(Dictionary<string, List<Instruction>?> bodies, string method, string callee, int depth)
    {
        if (!bodies.TryGetValue(method, out var code) || code is null) return false;
        if (code.Any(instruction => IsCall(instruction) && instruction.Name == callee)) return true;
        return depth > 1 && code.Where(instruction => (IsCall(instruction) || instruction.Code == OpCodes.Ldftn) && instruction.Name != method
                && instruction.Name!.StartsWith("MatrixDesktop.", StringComparison.Ordinal))
            .Select(instruction => instruction.Name!).Distinct().ToList().Any(next => Leads(bodies, next, callee, depth - 1));
    }

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
