using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using MatrixDesktop;

internal static class ForegroundActivationTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Native foreground activation preserves its independent native request order", RequestOrder),
        ("Native foreground activation never joins foreign input queues", IndependentQueues),
    ];

    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) yield break;
        for (var offset = 0; offset < bytes.Length;)
        {
            short value = bytes[offset++];
            if (value == 0xfe) value = (short)(0xfe00 | bytes[offset++]);
            var code = Codes[value];
            if (code.OperandType == OperandType.InlineMethod)
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset),
                    method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null)!;
            offset += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + BitConverter.ToInt32(bytes, offset) * 4,
                _ => 4,
            };
        }
    }

    private static void RequestOrder()
    {
        var method = typeof(ForegroundWindow).GetMethod("ForceForegroundWindow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var calls = Calls(method).ToArray();
        Require(calls.Select(call => call.Name).SequenceEqual(new[] { "SetForegroundWindow", "BringWindowToTop", "SetActiveWindow" }),
            "The compiled activation request must attempt foreground, z-order and local activation in order without foreign queue operations.");
        Require(calls.All(call => call.DeclaringType == typeof(ForegroundWindow) && call.GetCustomAttribute<DllImportAttribute>() is not null),
            "The activation-order witness must resolve the actual native imports.");
    }

    private static void IndependentQueues()
    {
        var start = typeof(ForegroundWindow).GetMethod(nameof(ForegroundWindow.BestEffortBringToFront))!;
        var pending = new Stack<MethodBase>([start]);
        var visited = new HashSet<MethodBase>();
        var native = new HashSet<string>();
        while (pending.TryPop(out var method))
        {
            if (!visited.Add(method)) continue;
            foreach (var called in Calls(method))
            {
                var import = called.GetCustomAttribute<DllImportAttribute>();
                if (import is not null) native.Add(import.EntryPoint ?? called.Name);
                if (called.DeclaringType == typeof(ForegroundWindow) && import is null) pending.Push(called);
            }
        }
        Require(native.Contains("SetForegroundWindow") && native.Contains("BringWindowToTop") && native.Contains("SetActiveWindow"),
            "The reachable compiled helper must retain real activation attempts.");
        Require(!native.Contains("AttachThreadInput"), "The reachable compiled helper must never join a foreign input queue.");
    }
}
