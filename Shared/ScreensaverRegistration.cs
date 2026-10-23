using System;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Shared;

// Registration is a deliberate product action, never a preview side effect.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class ScreensaverRegistration
{
    private const string DesktopKey = @"Control Panel\Desktop";
    private const string BackupKey = @"Software\MatrixDesktop\ScreensaverBackup";
    private static readonly string[] Values = ["SCRNSAVE.EXE", "ScreenSaveActive"];

    public static void Register(string scrPath)
    {
        scrPath = Path.GetFullPath(scrPath);
        if (!File.Exists(scrPath) || !scrPath.EndsWith(".scr", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("The MatrixDesktop screensaver is missing. Publish or build the complete application first.", scrPath);
        using var desktop = Registry.CurrentUser.CreateSubKey(DesktopKey);
        using var backup = Registry.CurrentUser.CreateSubKey(BackupKey);
        RegisterWithKeys(scrPath, desktop, backup, GetActive(), SetActive);
    }

    internal static void RegisterWithKeys(string scrPath, RegistryKey desktop, RegistryKey backup, bool previousActive, Action<bool> setActive)
    {
        var before = Capture(desktop);
        var savePrevious = backup.GetValue("Saved") is null || !string.Equals(
            desktop.GetValue("SCRNSAVE.EXE") as string, backup.GetValue("RegisteredPath") as string,
            StringComparison.OrdinalIgnoreCase);
        if (savePrevious)
        {
            backup.DeleteValue("Saved", throwOnMissingValue: false);
            backup.DeleteValue("RegisteredPath", throwOnMissingValue: false);
            foreach (var name in Values)
            {
                var value = desktop.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                backup.SetValue(name + ".present", value is null ? 0 : 1, RegistryValueKind.DWord);
                if (value is not null)
                {
                    backup.SetValue(name, value, desktop.GetValueKind(name));
                    backup.SetValue(name + ".kind", (int)desktop.GetValueKind(name), RegistryValueKind.DWord);
                }
            }
            backup.SetValue("RuntimeActive", previousActive ? 1 : 0, RegistryValueKind.DWord);
            backup.SetValue("Saved", 1, RegistryValueKind.DWord);
        }
        try
        {
            desktop.SetValue("SCRNSAVE.EXE", scrPath, RegistryValueKind.String);
            desktop.SetValue("ScreenSaveActive", "1", RegistryValueKind.String);
            setActive(true);
            backup.SetValue("RegisteredPath", scrPath, RegistryValueKind.String);
        }
        catch
        {
            try { setActive(previousActive); }
            finally
            {
                RestoreValues(desktop, before);
                if (savePrevious) backup.DeleteValue("Saved", throwOnMissingValue: false);
            }
            throw;
        }
    }

    public static void Restore()
    {
        using var backup = Registry.CurrentUser.OpenSubKey(BackupKey);
        if (backup?.GetValue("Saved") is null) return;
        using var desktop = Registry.CurrentUser.CreateSubKey(DesktopKey);
        RestoreWithKeys(desktop, backup, GetActive(), SetActive);
        backup.Close();
        Registry.CurrentUser.DeleteSubKeyTree(BackupKey, throwOnMissingSubKey: false);
    }

    internal static void RestoreWithKeys(RegistryKey desktop, RegistryKey backup, bool currentActive, Action<bool> setActive)
    {
        // Do not overwrite a replacement the owner selected after MatrixDesktop.
        var ours = backup.GetValue("RegisteredPath") as string;
        var current = desktop.GetValue("SCRNSAVE.EXE") as string;
        if (!string.Equals(current, ours, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The current screensaver has changed since MatrixDesktop was selected. The saved previous choice was retained.");
        var restored = new System.Collections.Generic.Dictionary<string, (object? Value, RegistryValueKind Kind)>();
        foreach (var name in Values)
        {
            if (backup.GetValue(name + ".present") is int present && present == 1)
            {
                var value = backup.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                    ?? throw new InvalidDataException("The previous screensaver backup is incomplete.");
                var kind = backup.GetValue(name + ".kind") is int savedKind ? (RegistryValueKind)savedKind : RegistryValueKind.String;
                restored[name] = (value, kind);
            }
            else restored[name] = (null, RegistryValueKind.String);
        }
        var before = Capture(desktop);
        var previousActive = backup.GetValue("RuntimeActive") is int active ? active == 1
            : restored["ScreenSaveActive"].Value is string text && text == "1";
        try
        {
            setActive(previousActive);
            RestoreValues(desktop, restored);
        }
        catch
        {
            try { setActive(currentActive); }
            finally { RestoreValues(desktop, before); }
            throw;
        }
    }

    private static System.Collections.Generic.Dictionary<string, (object? Value, RegistryValueKind Kind)> Capture(RegistryKey key)
    {
        var snapshot = new System.Collections.Generic.Dictionary<string, (object?, RegistryValueKind)>();
        foreach (var name in Values)
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            snapshot[name] = (value, value is null ? RegistryValueKind.String : key.GetValueKind(name));
        }
        return snapshot;
    }

    private static void RestoreValues(RegistryKey key, System.Collections.Generic.Dictionary<string, (object? Value, RegistryValueKind Kind)> snapshot)
    {
        foreach (var (name, saved) in snapshot)
            if (saved.Value is null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, saved.Value, saved.Kind);
    }

    private static bool GetActive()
    {
        if (!SystemParametersInfoGet(0x0010, 0, out var active, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return active;
    }

    private static void SetActive(bool active)
    {
        // Update the live system parameter as well as persisted registry state.
        if (!SystemParametersInfoSet(0x0011, active ? 1u : 0u, IntPtr.Zero, 3))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not update screensaver activation.");
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoGet(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoSet(uint action, uint parameter, IntPtr value, uint flags);
}
