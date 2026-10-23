using System.Text.Json;
using MatrixDesktopConfigurator;

namespace MatrixDesktop.Tests;

internal static class DraftCloseFlushTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("Configurator close captures the latest draft once across repeated close requests", () => LatestDraft().GetAwaiter().GetResult()),
        ("Configurator close bounds unavailable snapshots and ignores late completions", () => Unavailable().GetAwaiter().GetResult()),
    ];

    private static async Task LatestDraft()
    {
        var flush = new DraftCloseFlush();
        var capture = new TaskCompletionSource<string>();
        var captures = 0;
        var saves = new List<JsonElement>();
        var warnings = new List<string>();
        Task<string> Snapshot() { captures++; return capture.Task; }
        var first = flush.FlushAsync(Snapshot, payload => saves.Add(payload.Clone()), warnings.Add);
        var repeated = flush.FlushAsync(Snapshot, payload => saves.Add(payload.Clone()), warnings.Add);
        Check.True(ReferenceEquals(first, repeated), "repeated close requests must await the same one-time flush");
        Check.False(flush.Completed, "native disposal must wait while the snapshot is pending");
        capture.SetResult("""{"draft":{"scene":"zmatrix","zmMaxStream":321},"selectedPresetId":"owner-preset"}""");
        await first;
        Check.Equal(1, captures, "only one frontend snapshot may be requested");
        Check.Equal(1, saves.Count, "the latest snapshot must be applied through the native save path once");
        Check.Equal(321, saves[0].GetProperty("draft").GetProperty("zmMaxStream").GetInt32(), "the flushed edit must replace the older host draft");
        Check.Equal("owner-preset", saves[0].GetProperty("selectedPresetId").GetString()!, "the selected preset belongs to the same snapshot");
        Check.True(flush.Completed && warnings.Count == 0, "successful shutdown must finish without a fallback warning");
    }

    private static async Task Unavailable()
    {
        var failedCapture = new TaskCompletionSource<string>();
        var observed = DraftCloseFlush.CaptureAsync(() => failedCapture.Task);
        failedCapture.SetException(new InvalidOperationException("A late COM failure must be observed."));
        var observedResult = await observed;
        Check.True(observedResult.Error is InvalidOperationException && !observed.IsFaulted, "the capture wrapper must observe faults even when its caller has already timed out");
        foreach (var scenario in new[] { "timeout", "late-fault", "unavailable", "invalid", "malformed" })
        {
            var flush = new DraftCloseFlush();
            var late = new TaskCompletionSource<string>();
            var saves = 0;
            var warnings = new List<string>();
            Task<string> Capture() => scenario is "timeout" or "late-fault" ? late.Task : scenario == "unavailable"
                ? throw new InvalidOperationException("WebView2 is unavailable.") : Task.FromResult(scenario == "malformed" ? "{bad" : "null");
            var operation = flush.FlushAsync(Capture, _ => saves++, warnings.Add, timeoutMilliseconds: 30);
            Check.True(await Task.WhenAny(operation, Task.Delay(2_000)) == operation, "a stalled renderer must not prevent native close");
            await operation;
            if (scenario == "late-fault") late.TrySetException(new InvalidOperationException("Disposed renderer failed after timeout."));
            else late.TrySetResult("""{"draft":{"zmMaxStream":999}}""");
            await Task.Yield();
            Check.True(flush.Completed, "fallback must allow native disposal");
            Check.Equal(0, saves, "unavailable or late snapshots must not overwrite the last received draft");
            Check.True(warnings.Count == 1 && warnings[0].Contains("last received draft", StringComparison.Ordinal), "each fallback must report why the existing host draft was retained");
        }
    }
}
