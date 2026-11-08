using MatrixDesktop.Shared;
using Microsoft.Web.WebView2.Core;

namespace MatrixDesktop;

// The WebView2 permission requests the host answers itself. Pure, so the native harness can
// drive it without a WebView; MainForm applies the answer.
internal static class HostPermissionPolicy
{
    internal readonly record struct Answer(bool Handled, CoreWebView2PermissionState State, bool SavesInProfile);

    // R2-51. A wallpaper, /s or /p host cannot show WebView2's camera prompt: no input reaches
    // the wallpaper or the preview, and /s exits on the first input. While the prompt waits,
    // getUserMedia never settles and the renderer awaits it before its first frame, so Mirror +
    // Camera drew nothing. These hosts deny the camera and the microphone without UI and without
    // saving: getUserMedia rejects at once, a saved Allow is kept (WebView2 then grants it without
    // raising the event), and the next interactive launch can still prompt. Interactive hosts get
    // no answer and keep the prompt.
    internal static Answer Decide(NativeHostKind host, CoreWebView2PermissionKind kind) =>
        CannotPrompt(host) && kind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone
            ? new(Handled: true, CoreWebView2PermissionState.Deny, SavesInProfile: false)
            : default;

    // The same three host kinds the bridge's bootstrap marks interactive: false.
    private static bool CannotPrompt(NativeHostKind host) =>
        host is NativeHostKind.Wallpaper or NativeHostKind.Screensaver or NativeHostKind.ScreensaverPreview;
}
