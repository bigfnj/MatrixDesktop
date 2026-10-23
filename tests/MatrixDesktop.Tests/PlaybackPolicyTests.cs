using System.Drawing;

namespace MatrixDesktop.Tests;

internal static class PlaybackPolicyTests
{
    internal static IEnumerable<(string, Action)> All =>
    [
        ("Wallpaper playback keeps independent pause reasons", IndependentReasons),
        ("Wallpaper fullscreen policy pauses for any individual monitor", IndividualMonitor),
        ("Wallpaper priority auto preserves the standalone default", AutomaticPriority),
    ];

    private static void IndependentReasons()
    {
        var reasons = Enum.GetValues<PauseReason>().Where(reason => reason != PauseReason.None).ToArray();
        foreach (var retained in reasons) foreach (var removed in reasons.Where(reason => reason != retained))
        {
            var policy = new PlaybackPolicy();
            policy.Set(retained, true); policy.Set(removed, true); policy.Set(removed, false);
            Check.True(policy.Paused, $"clearing {removed} cannot override {retained}");
            Check.Equal(retained, policy.Reasons, "the remaining cause must survive exactly");
            Check.False(policy.Set(removed, false), "repeated clearing must be idempotent");
            policy.Set(retained, false);
            Check.False(policy.Paused, "playback resumes after the final reason clears");
        }
    }

    private static void IndividualMonitor()
    {
        var screens = new[] { new Rectangle(-1920, 0, 1920, 1080), new Rectangle(0, 0, 2560, 1440) };
        Check.True(PlaybackPolicy.CoversAnyMonitor(new Rectangle(-1920, 0, 1920, 1080), screens), "fullscreen on the left monitor must pause even though the other monitor is uncovered");
        Check.True(PlaybackPolicy.CoversAnyMonitor(new Rectangle(0, 0, 2560, 1440), screens), "fullscreen on the right monitor must pause");
        Check.False(PlaybackPolicy.CoversAnyMonitor(new Rectangle(-1900, 20, 1800, 1000), screens), "an ordinary window on the negative-coordinate display must not pause");
        Check.False(PlaybackPolicy.CoversAnyMonitor(new Rectangle(-100, 0, 500, 500), screens), "a window straddling displays is not necessarily fullscreen");
        Check.False(PlaybackPolicy.CoversAnyMonitor(new Rectangle(0, 0, 2560, 1440), []), "no monitor must not produce a vacuous fullscreen match");
    }

    private static void AutomaticPriority()
    {
        Check.Equal("normal", PlaybackPolicy.ResolvePriority("auto", false), "existing standalone launches retain normal priority");
        Check.Equal("idle", PlaybackPolicy.ResolvePriority("auto", true), "wallpaper defaults to idle priority");
        foreach (var priority in new[] { "idle", "below-normal", "normal", "above-normal", "high", "realtime" })
        {
            Check.Equal(priority, PlaybackPolicy.ResolvePriority(priority, false), "an explicit standalone priority survives");
            Check.Equal(priority, PlaybackPolicy.ResolvePriority(priority, true), "an explicit wallpaper priority survives");
        }
    }
}
