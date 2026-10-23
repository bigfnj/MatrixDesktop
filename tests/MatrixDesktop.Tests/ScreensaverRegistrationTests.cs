using Microsoft.Win32;
using Shared;

namespace MatrixDesktop.Tests;

internal static class ScreensaverRegistrationTests
{
    public static IEnumerable<(string, Action)> All => OperatingSystem.IsWindows()
        ? [("Screensaver registration preserves the previous choice and live activation", RoundTrip),
           ("Screensaver restore preserves a later owner selection", OwnerReplacement),
           ("Explicit screensaver re-registration backs up the latest owner choice", Reregistration),
           ("Failed screensaver activation rolls back the current selection", FailedActivation)]
        : [];

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WithKeys(Action<RegistryKey, RegistryKey> test)
    {
        var path = @"Software\MatrixDesktop\TestFixtures\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            using var desktop = root.CreateSubKey("Desktop");
            using var backup = root.CreateSubKey("Backup");
            test(desktop, backup);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RoundTrip() => WithKeys((desktop, backup) =>
    {
        desktop.SetValue("SCRNSAVE.EXE", @"%SystemRoot%\synthetic-old.scr", RegistryValueKind.ExpandString);
        desktop.SetValue("ScreenSaveActive", "0", RegistryValueKind.String);
        var activation = new List<bool>();
        ScreensaverRegistration.RegisterWithKeys(@"C:\synthetic\MatrixDesktop.scr", desktop, backup, false, activation.Add);
        ScreensaverRegistration.RegisterWithKeys(@"C:\synthetic-v2\MatrixDesktop.scr", desktop, backup, true, activation.Add);
        ScreensaverRegistration.RestoreWithKeys(desktop, backup, true, activation.Add);
        Check.Equal(@"%SystemRoot%\synthetic-old.scr", (string)desktop.GetValue("SCRNSAVE.EXE", null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, "restore must preserve unexpanded text");
        Check.Equal(RegistryValueKind.ExpandString, desktop.GetValueKind("SCRNSAVE.EXE"), "restore must preserve value kind");
        Check.Equal("0", (string)desktop.GetValue("ScreenSaveActive")!, "restore must preserve activation text");
        Check.True(activation.SequenceEqual([true, true, false]), "registration and restore must update live Windows activation and retain the first backup");
    });

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void OwnerReplacement() => WithKeys((desktop, backup) =>
    {
        ScreensaverRegistration.RegisterWithKeys(@"C:\synthetic\MatrixDesktop.scr", desktop, backup, false, _ => { });
        desktop.SetValue("SCRNSAVE.EXE", @"C:\synthetic-owner-choice.scr");
        var rejected = false;
        try { ScreensaverRegistration.RestoreWithKeys(desktop, backup, true, _ => throw new Exception("activation must not run after ownership changes")); }
        catch (InvalidOperationException) { rejected = true; }
        Check.True(rejected, "restore must refuse to replace the owner's later selection");
        Check.Equal(@"C:\synthetic-owner-choice.scr", (string)desktop.GetValue("SCRNSAVE.EXE")!, "owner replacement must survive restore");
        Check.Equal(1, (int)backup.GetValue("Saved")!, "rejected restore must retain its backup");
    });

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void FailedActivation() => WithKeys((desktop, backup) =>
    {
        var rejected = false;
        try
        {
            ScreensaverRegistration.RegisterWithKeys(@"C:\synthetic\MatrixDesktop.scr", desktop, backup, false,
                active => { if (active) throw new InvalidOperationException("synthetic activation failure"); });
        }
        catch (InvalidOperationException) { rejected = true; }
        Check.True(rejected, "a failed native activation must surface its failure");
        Check.True(desktop.GetValue("SCRNSAVE.EXE") is null && desktop.GetValue("ScreenSaveActive") is null,
            "failed registration must restore originally absent registry values");
        Check.True(backup.GetValue("Saved") is null, "failed initial registration must not retain a committed backup marker");
    });

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Reregistration() => WithKeys((desktop, backup) =>
    {
        desktop.SetValue("SCRNSAVE.EXE", @"C:\synthetic-first.scr");
        ScreensaverRegistration.RegisterWithKeys(@"C:\synthetic\MatrixDesktop.scr", desktop, backup, false, _ => { });
        desktop.SetValue("SCRNSAVE.EXE", @"C:\synthetic-second.scr");
        ScreensaverRegistration.RegisterWithKeys(@"C:\synthetic\MatrixDesktop.scr", desktop, backup, true, _ => { });
        ScreensaverRegistration.RestoreWithKeys(desktop, backup, true, _ => { });
        Check.Equal(@"C:\synthetic-second.scr", (string)desktop.GetValue("SCRNSAVE.EXE")!,
            "explicit re-registration must back up the owner's latest choice rather than an obsolete first choice");
    });
}
