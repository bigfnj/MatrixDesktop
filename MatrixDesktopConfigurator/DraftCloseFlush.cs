using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace MatrixDesktopConfigurator;

// One bounded snapshot at shutdown complements debounced normal writes. The
// native form owns disposal and re-enters Close only after this attempt ends.
internal sealed class DraftCloseFlush
{
    private Task? _pending;
    public bool Completed { get; private set; }

    public Task FlushAsync(Func<Task<string>> capture, Action<JsonElement> save, Action<string> warn, int timeoutMilliseconds = 1500)
        => _pending ??= RunAsync(capture, save, warn, timeoutMilliseconds);

    private async Task RunAsync(Func<Task<string>> capture, Action<JsonElement> save, Action<string> warn, int timeoutMilliseconds)
    {
        try
        {
            // The wrapper always observes capture failures, even if the timeout
            // already allowed native disposal and COM faults arrive afterward.
            var snapshot = CaptureAsync(capture);
            if (await Task.WhenAny(snapshot, Task.Delay(timeoutMilliseconds)) != snapshot)
                throw new TimeoutException("The closing draft snapshot exceeded its time limit.");
            var (json, error) = await snapshot;
            if (error is not null) throw new InvalidOperationException(error.Message, error);
            using var document = JsonDocument.Parse(json!);
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("The closing draft snapshot was not available.");
            save(payload);
        }
        catch (Exception error)
        {
            warn("Configurator close retained the last received draft: " + error.Message);
        }
        finally { Completed = true; }
    }

    internal static async Task<(string? Json, Exception? Error)> CaptureAsync(Func<Task<string>> capture)
    {
        try { return (await capture(), null); }
        catch (Exception error) { return (null, error); }
    }
}
