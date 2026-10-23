namespace MatrixDesktop;

internal enum HostLaunchKind { Standard, Screensaver, ScreensaverPreview }

internal sealed record HostLaunchPlan(
    string[] Arguments,
    HostLaunchKind Kind = HostLaunchKind.Standard,
    nint PreviewParent = default)
{
    internal bool IsScreensaver => Kind == HostLaunchKind.Screensaver;
    internal bool IsPreview => Kind == HostLaunchKind.ScreensaverPreview;
}
