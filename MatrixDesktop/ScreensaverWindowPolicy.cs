namespace MatrixDesktop;

internal static class ScreensaverWindowPolicy
{
    internal static string[] EffectiveArguments(HostLaunchPlan plan) => plan.IsScreensaver
        ? plan.Arguments.Concat(["--borderless", "--working-area=false", "--hide-cursor", "--exit-on-any-key", "--exit-on-esc", "--global-key-exit", "--topmost"]).ToArray()
        : plan.Arguments;

    internal static void RendererReady(HostLaunchPlan plan, Action notifyReady, Action requestForeground)
    {
        notifyReady();
        if (plan.IsScreensaver) requestForeground();
    }

    internal static bool StopAfterOwnership(HostLaunchPlan plan, bool ownsForeground)
        => ownsForeground && !plan.IsScreensaver;
}
