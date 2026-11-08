using System.Text.Json;
using MatrixDesktop;
using MatrixDesktop.Shared;
using Microsoft.Web.WebView2.Core;

// The permission answers MainForm applies, for the host kind MainForm passes (R2-51), and the
// saved permission state it clears before each navigation (R2-52).
internal static class HostPermissionTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Host permission policy answers camera and microphone prompts only for hosts that cannot show them", AnswersHostsThatCannotPrompt),
        ("Host permission policy forgets only a saved camera denial for the app's own origin", ForgetsOnlyOwnCameraDenial),
    ];

    // MainForm's HostOriginPrefix.
    private const string AppOrigin = "https://appassets.local/";

    private static void ForgetsOnlyOwnCameraDenial()
    {
        const CoreWebView2PermissionKind camera = CoreWebView2PermissionKind.Camera;
        const CoreWebView2PermissionState deny = CoreWebView2PermissionState.Deny;
        foreach (var origin in new[] { "https://appassets.local", "https://appassets.local/", "https://APPASSETS.local/index.html?effect=mirror&camera=true" })
            Require(HostPermissionPolicy.ForgetsSavedState(camera, deny, origin, AppOrigin), $"A saved camera denial for '{origin}' is this app's and must be cleared, or one Block disables the camera for good.");
        Require(!HostPermissionPolicy.ForgetsSavedState(camera, CoreWebView2PermissionState.Allow, "https://appassets.local", AppOrigin),
            "A saved camera Allow must be kept: it is the only way a wallpaper, /s or /p host gets the camera.");
        Require(!HostPermissionPolicy.ForgetsSavedState(CoreWebView2PermissionKind.Microphone, deny, "https://appassets.local", AppOrigin),
            "Only the camera, which the Camera setting asks for, is cleared.");
        foreach (var origin in new string?[] { "https://example.com", "http://appassets.local", "https://appassets.local.example.com", "https://appassets.local:8443", "", "not an origin", null })
            Require(!HostPermissionPolicy.ForgetsSavedState(camera, deny, origin, AppOrigin), $"A saved denial for '{origin ?? "(null)"}' is not this app's origin and must be left alone.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void AnswersHostsThatCannotPrompt()
    {
        foreach (var (name, launch, wallpaperWindowMode, answers) in new[]
        {
            ("fullscreen, windowed or borderless", HostLaunchKind.Standard, false, false),
            ("wallpaper", HostLaunchKind.Standard, true, true),
            ("screensaver (/s)", HostLaunchKind.Screensaver, false, true),
            ("screensaver preview (/p)", HostLaunchKind.ScreensaverPreview, false, true),
        })
        {
            var host = MediaHostKind.For(new HostLaunchPlan([], launch), wallpaperWindowMode);
            foreach (var kind in new[] { CoreWebView2PermissionKind.Camera, CoreWebView2PermissionKind.Microphone })
            {
                var answer = HostPermissionPolicy.Decide(host, kind);
                if (!answers)
                {
                    Require(!answer.Handled, $"The {name} host must leave {kind} to WebView2's own prompt.");
                    continue;
                }
                Require(answer.Handled, $"The {name} host cannot show WebView2's prompt, so it must answer {kind} itself.");
                Require(answer.State == CoreWebView2PermissionState.Deny, $"The {name} host must deny {kind}, got {answer.State}.");
                Require(!answer.SavesInProfile, $"The {name} host's {kind} denial must not be saved: it would replace a saved Allow and silence the next interactive prompt.");
            }
            // Nothing the page never asks for: those keep WebView2's own handling.
            foreach (var kind in new[] { CoreWebView2PermissionKind.Geolocation, CoreWebView2PermissionKind.Notifications, CoreWebView2PermissionKind.Autoplay, CoreWebView2PermissionKind.UnknownPermission })
                Require(!HostPermissionPolicy.Decide(host, kind).Handled, $"The {name} host must not answer {kind}.");
        }

        // The page's view of the same question: the bridge's bootstrap says interactive: false
        // for exactly the hosts that must answer without a prompt.
        foreach (var host in Enum.GetValues<NativeHostKind>())
        {
            using var bridge = new NativeMediaBridge(false, host);
            bridge.Prepare("scene=matrix", new System.Drawing.Rectangle(0, 0, 32, 32));
            var interactive = JsonDocument.Parse(bridge.BootstrapJson()).RootElement.GetProperty("interactive").GetBoolean();
            Require(HostPermissionPolicy.Decide(host, CoreWebView2PermissionKind.Camera).Handled == !interactive,
                $"The {host} host must answer the camera prompt exactly when its bootstrap says no input reaches it (interactive={interactive}).");
        }
    }
}
