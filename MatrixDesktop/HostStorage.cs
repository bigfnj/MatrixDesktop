namespace MatrixDesktop;

// Where the host keeps its WebView2 profile. Free of WinForms and WebView2 types so the core
// harness can link it.
internal static class HostStorage
{
    // Logged once per WebView2 initialisation. A WEBVIEW2_USER_DATA_FOLDER override replaces
    // the folder the product asks for, so a check that only looks beside the exe cannot see a
    // wrong request; the gate reads both halves of this record instead. Keep the format: the
    // gate parses it.
    internal static string ProfileRecord(string? requested, string? effective) =>
        $"WebView2 profile requested={requested} effective={effective}";

    // First writable <base>\MatrixDesktop\<child>, in base order. Choosing any base but the
    // first splits the profile and the staged web root across locations, so it is reported.
    internal static string? SelectWritableFolder(IReadOnlyList<string?> basePaths, string child, Action<string> warn)
    {
        for (var index = 0; index < basePaths.Count; index++)
        {
            var basePath = basePaths[index];
            if (string.IsNullOrWhiteSpace(basePath)) continue;
            var path = Path.Combine(basePath, "MatrixDesktop", child);
            if (!TryEnsureWritableFolder(path)) continue;
            if (index > 0) warn($"The preferred folder for '{child}' under '{basePaths[0]}' was not writable; using '{path}'.");
            return path;
        }
        return null;
    }

    // Each attempt probes its own file. A fixed probe name made concurrent startups fail each
    // other's probe (sharing violation or delete-pending), which silently moved the losing
    // instance's profile and web root to Roaming AppData. DeleteOnClose removes the probe
    // even if the process dies while it is open. whileOpen is a test seam that runs while
    // the probe is held.
    internal static bool TryEnsureWritableFolder(string path, Action<string>? whileOpen = null)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write_test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte((byte)'o');
                stream.Flush();
                whileOpen?.Invoke(probe);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
