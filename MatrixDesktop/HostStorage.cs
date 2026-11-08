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
}
