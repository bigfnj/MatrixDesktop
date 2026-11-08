using MatrixDesktop.Shared;

namespace MatrixDesktop;

// The bridge's host kind for a launch. /s and /p win over the wallpaper window mode,
// exactly as MainForm.IsWallpaper excludes them.
internal static class MediaHostKind
{
    internal static NativeHostKind For(HostLaunchPlan plan, bool wallpaperWindowMode) => plan.Kind switch
    {
        HostLaunchKind.Screensaver => NativeHostKind.Screensaver,
        HostLaunchKind.ScreensaverPreview => NativeHostKind.ScreensaverPreview,
        _ => wallpaperWindowMode ? NativeHostKind.Wallpaper : NativeHostKind.Interactive,
    };
}
