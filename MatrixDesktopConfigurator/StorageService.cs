using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatrixDesktop.Shared;
using Microsoft.Win32.SafeHandles;

namespace MatrixDesktopConfigurator;

internal sealed class StorageService
{
    private const string FileName = "MatrixDesktopConfigurator.presets.json";
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string StoragePath { get; }
    public bool UsesPortablePath { get; }

    // Last known-good copy, rotated in by Save and read back by Load when the live file
    // cannot be parsed.
    public string BackupPath => StoragePath + ".bak";

    public StorageService()
    {
        (StoragePath, UsesPortablePath) = ResolveStoragePath();
    }

    // Synthetic storage tests use an owned path without probing a user's store.
    internal StorageService(string storagePath)
    {
        StoragePath = Path.GetFullPath(storagePath);
        UsesPortablePath = false;
    }

    public ConfiguratorState Load()
    {
        try
        {
            if (!File.Exists(StoragePath))
            {
                return TryLoadBackup() ?? new ConfiguratorState();
            }

            var json = File.ReadAllText(StoragePath);

            // MD-06: an empty file is not corruption, it is what a first run looks like
            // when something has touched the path without writing to it. Treating it as an
            // ERROR trained the reader to ignore a channel with only six call sites in the
            // entire product.
            if (string.IsNullOrWhiteSpace(json))
            {
                return TryLoadBackup() ?? new ConfiguratorState();
            }

            var state = JsonSerializer.Deserialize<ConfiguratorState>(json, _jsonOptions);
            if (state is null)
            {
                // Deserialize returning null means the file existed but its top-level
                // JSON was literal `null`.
                Logger.Warn($"Configurator state file deserialized to null. Path='{StoragePath}'. Starting from fresh state.");
                return TryLoadBackup() ?? new ConfiguratorState();
            }

            return Normalize(state);
        }
        catch (Exception ex)
        {
            // Genuinely unreadable: corrupt JSON, an encoding error, a torn write.
            //
            // MD-05: the comment that used to sit here told the reader they could restore
            // from "the preserved file", and nothing preserved anything. The unreadable
            // file stayed where it was and the next Save overwrote it, so the presets were
            // gone for good. Now the wreckage is moved aside before anything can overwrite
            // it, and the last known-good backup is tried first.
            var quarantined = TryQuarantineUnreadableFile();
            var recovered = TryLoadBackup();

            if (recovered is not null)
            {
                Logger.Warn(
                    $"Configurator state at '{StoragePath}' was unreadable ({ex.GetType().Name}); recovered the " +
                    $"previous good copy from '{BackupPath}'. The unreadable file was kept at " +
                    $"'{quarantined ?? "(not preserved)"}'.");
                return recovered;
            }

            Logger.Error(
                $"Failed to load configurator state from '{StoragePath}' and no usable backup exists. Starting " +
                $"from fresh state. The unreadable file was kept at '{quarantined ?? "(not preserved)"}'.",
                ex);
            return new ConfiguratorState();
        }
    }

    // MD-05: write to a temp file, then swap it in, keeping the previous copy as a backup.
    // The old implementation wrote straight onto the live path, so a process death or a
    // full disk part way through left a truncated file that Load could not parse and every
    // saved preset was gone. A save happens on every debounced edit, so that window was not
    // small.
    public void Save(ConfiguratorState state)
    {
        var directory = Path.GetDirectoryName(StoragePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(state, _jsonOptions);
        var temp = StoragePath + ".tmp";

        try
        {
            File.WriteAllText(temp, json);
        }
        catch
        {
            TryDeleteTemp(temp);
            throw;
        }

        if (File.Exists(StoragePath))
        {
            try
            {
                // Atomic where the filesystem supports it, and it rotates the previous
                // contents into the backup in the same call.
                File.Replace(temp, StoragePath, BackupPath, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Replace refuses across volumes and on some network paths. Fall back to a
                // copy-then-move, which is still ordered so the live file is never the
                // half-written one.
                try { File.Copy(StoragePath, BackupPath, overwrite: true); } catch { /* best effort */ }
            }
        }

        File.Move(temp, StoragePath, overwrite: true);
    }

    // Best-effort sweep of a temp file left by a failed save. Harmless if it survives, since
    // the next save truncates it, but it is confusing to find next to the presets.
    private void TryDeleteTemp(string temp)
    {
        try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
    }

    public static JsonObject CloneObject(JsonObject? source)
    {
        if (source is null)
        {
            return [];
        }

        return JsonNode.Parse(source.ToJsonString())?.AsObject() ?? [];
    }

    private static ConfiguratorState Normalize(ConfiguratorState state)
    {
        state.SchemaVersion = Math.Max(1, state.SchemaVersion);
        state.PresetSeedVersion = Math.Max(0, state.PresetSeedVersion);
        state.LastDraft ??= [];
        state.UserPresets ??= [];
        // Valid JSON can still contain null records or a null values object.
        // Preserve every real preset and repair only its missing container.
        state.UserPresets.RemoveAll(preset => preset is null);
        foreach (var preset in state.UserPresets) preset.Values ??= [];
        return state;
    }

    private ConfiguratorState? TryLoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;

            var json = File.ReadAllText(BackupPath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var state = JsonSerializer.Deserialize<ConfiguratorState>(json, _jsonOptions);
            return state is null ? null : Normalize(state);
        }
        catch
        {
            // A bad backup is not worth reporting on top of the bad live file that sent us
            // here; the caller logs that.
            return null;
        }
    }

    private string? TryQuarantineUnreadableFile()
    {
        try
        {
            if (!File.Exists(StoragePath)) return null;

            // Milliseconds, and no overwrite. At second resolution with overwrite:true, two
            // configurators started together both hitting an unreadable file would quarantine
            // to the same name and the second would destroy the first casualty, which defeats
            // the point of preserving it. The same collision was fixed in CrashDumpWriter.
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss.fff", CultureInfo.InvariantCulture);
            var target = StoragePath + ".unreadable-" + stamp;
            if (File.Exists(target))
            {
                target = StoragePath + ".unreadable-" + stamp + "-" + Environment.ProcessId;
            }

            File.Move(StoragePath, target);
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static (string Path, bool Portable) ResolveStoragePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.GetTempPath();
        }

        return ResolveStoragePath(
            Path.Combine(AppContext.BaseDirectory, FileName),
            Path.Combine(appData, "MatrixDesktop", "Configurator", FileName),
            warn: Logger.Warn,
            degraded: Logger.Info);
    }

    // Program.cs takes the store-keyed single-instance guard AFTER this runs, so two launches
    // started together must resolve the same store here; otherwise each takes a different
    // store's guard, both open, and one saves to AppData (R2-18, MDR-04). Only a real refusal
    // falls back: no check below fails because another launch is probing or loading the
    // store at the same moment, or because the owner's Save has the store's name briefly
    // absent (R3-10). degraded hears every resolution in which the ACL check of an existing
    // store could not run in full and kept the kernel's write-attributes answer instead.
    // whileProbeOpen is a test seam that runs while the folder probe is held; afterStoreStep
    // runs after the existence check ("exists") and after the attribute check ("attributes") of
    // an existing store; storeRights replaces the rights the ACL evaluation granted on it.
    internal static (string Path, bool Portable) ResolveStoragePath(
        string portablePath, string appDataPath, Action<string>? whileProbeOpen = null, Action<string>? warn = null,
        Action<string>? afterStoreStep = null, Action<string>? degraded = null, Func<int?, int?>? storeRights = null)
    {
        if (CanWriteStorageFile(portablePath, whileProbeOpen, afterStoreStep, degraded, storeRights))
        {
            return (portablePath, true);
        }

        // Presets beside the executable that this launch will not show, which otherwise
        // reaches the owner as "my presets vanished".
        if (File.Exists(portablePath))
        {
            warn?.Invoke($"The portable preset store '{portablePath}' is not writable, so this launch uses '{appDataPath}' and does not show the presets saved beside the executable.");
        }

        return (appDataPath, false);
    }

    private static bool CanWriteStorageFile(
        string path, Action<string>? whileProbeOpen, Action<string>? afterStoreStep, Action<string>? degraded, Func<int?, int?>? storeRights)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            Directory.CreateDirectory(directory);

            // MD-06: this used FileMode.OpenOrCreate, so merely probing for writability
            // CREATED a zero-byte presets file beside the executable. Load then read it,
            // could not parse an empty document, and logged an ERROR on every genuinely
            // first run. The probe never creates the store.
            //
            // An existing store used to be proved writable by opening it ReadWrite. That open
            // was refused while another configurator's Load was reading the file, sending this
            // launch to AppData, and while it was held that Load failed, took the live store
            // for corrupt and moved it aside. The attribute and ACL checks answer the same
            // question without taking part in file sharing.
            if (File.Exists(path) && !ExistingStoreAllowsSaving(path, afterStoreStep, degraded, storeRights))
            {
                return false;
            }

            // Each attempt probes its own file, as HostStorage does (AUD-73). The fixed
            // '.write-probe' opened with FileShare.None made a launch overlapping another's
            // probe fail with a sharing violation and silently resolve to AppData.
            // DeleteOnClose removes the probe even when a scanner still holds it after our
            // close or the process dies holding it, so per-attempt names cannot pile up the
            // way the hand-deleted GUID probes once did.
            var probe = Path.Combine(directory, $".write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte((byte)'o');
                stream.Flush();
                whileProbeOpen?.Invoke(probe);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // An existing store is refused only for the ReadOnly attribute or an ACL that refuses
    // Save. A name that is gone by the time it is examined is the owner's Save between
    // File.Replace's two renames (the live store moved to .bak, the new copy not yet on its
    // name): that is a missing store, which the folder probe decides (R3-10).
    private static bool ExistingStoreAllowsSaving(
        string path, Action<string>? afterStoreStep, Action<string>? degraded, Func<int?, int?>? storeRights)
    {
        afterStoreStep?.Invoke("exists");
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            return false;
        }

        afterStoreStep?.Invoke("attributes");
        return !OperatingSystem.IsWindows() || AclAllowsWrites(path, degraded, storeRights);
    }

    // The store is opened for FILE_WRITE_ATTRIBUTES and READ_CONTROL only. Neither takes part
    // in file sharing, so this open neither waits for a reader nor makes one fail; opening for
    // DELETE, the right Save's rename needs, would fail with a sharing violation while Load reads
    // the store (R2-18). The raw call gets the extended-length path that .NET's own file APIs
    // add for themselves, or a store path of MAX_PATH or more reads as not found (R3-11). A
    // name that is not found is R3-10's rename gap, not a refusal; every other failure still
    // is, so an open that did take part in sharing would send a launch to AppData while the
    // store is being read, which ProbingLeavesReadersAlone catches.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool AclAllowsWrites(string path, Action<string>? degraded, Func<int?, int?>? storeRights)
    {
        using var handle = CreateFile(ExtendedLengthPath(path), FileWriteAttributes | ReadControl, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return Marshal.GetLastPInvokeError() is ErrorFileNotFound or ErrorPathNotFound;
        }

        var granted = MaximumAllowed(handle);
        return SavingAllowed(path, storeRights is null ? granted : storeRights(granted), () => FolderDeleteChildError(path), degraded);
    }

    // The verdict on an existing store whose open for FILE_WRITE_ATTRIBUTES the kernel granted.
    // That right alone is not the question Save asks: a hand-built ACL can keep it while denying
    // DELETE, and File.Replace and its File.Move fallback both rename the store away, which
    // needs DELETE on the store or FILE_DELETE_CHILD on its folder (R3-12, MD517-01). Save never
    // writes into the store: it writes a sibling temp file, which the folder probe's create
    // right covers, and renames it over, so the store's own data rights are not asked, and a
    // store that refuses them but can be renamed away is kept (MDR3-02). storeRights is what
    // AccessCheck grants this process under the store's DACL, or null when that could not be
    // read or evaluated; DELETE comes from it, never from opening for it.
    // It is trusted only where it agrees with the kernel about FILE_WRITE_ATTRIBUTES: a share
    // whose DACL names the server's own accounts, or a volume without ACLs, is not modelled by
    // the local token. FILE_DELETE_CHILD takes no part in file sharing, so the folder's answer
    // comes from the kernel (the server, on a share): folderDeleteChildError returns 0 when it
    // is granted, otherwise the win32 error of the open that asked. Wherever an answer is
    // missing, the kernel's write-attributes answer stands as it did before R3-12, and degraded
    // says so on that resolution, naming the store.
    internal static bool SavingAllowed(string path, int? storeRights, Func<int> folderDeleteChildError, Action<string>? degraded)
    {
        const string kept = "so the store is kept on the kernel's write-attributes answer alone, and a Save its ACL refuses is not caught here";
        if (storeRights is not int granted)
        {
            degraded?.Invoke($"The ACL of the portable preset store '{path}' could not be read or evaluated, {kept}.");
            return true;
        }

        if ((granted & FileWriteAttributes) == 0)
        {
            degraded?.Invoke($"The ACL of the portable preset store '{path}' grants this process no write attributes although the kernel did (a share whose ACL names the server's own accounts does this), {kept}.");
            return true;
        }

        if ((granted & Delete) != 0)
        {
            return true;
        }

        var error = folderDeleteChildError();
        if (error == ErrorAccessDenied)
        {
            return false;
        }

        if (error != 0)
        {
            degraded?.Invoke($"Whether the folder of the portable preset store '{path}' lets this process delete its files could not be asked (win32 {error}), {kept}.");
        }

        return true;
    }

    // 0 when the kernel grants this process FILE_DELETE_CHILD on the store's folder, otherwise
    // the win32 error of the open that asked (-1 if the open failed without one).
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int FolderDeleteChildError(string path)
    {
        using var folder = CreateFile(ExtendedLengthPath(Path.GetDirectoryName(Path.GetFullPath(path))!), FileDeleteChild, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagBackupSemantics, IntPtr.Zero);
        if (!folder.IsInvalid)
        {
            return 0;
        }

        var error = Marshal.GetLastPInvokeError();
        return error == 0 ? -1 : error;
    }

    // The rights AccessCheck grants this process's token under the handle's owner, group and
    // DACL, or null when they cannot be read or evaluated.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int? MaximumAllowed(SafeFileHandle handle)
    {
        if (GetSecurityInfo(handle, SeFileObject, OwnerGroupDaclInformation, out _, out _, out _, out _, out var descriptor) != 0)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenDuplicate, out var process))
            {
                return null;
            }

            using (process)
            {
                if (!DuplicateToken(process, SecurityImpersonation, out var token))
                {
                    return null;
                }

                using (token)
                {
                    var mapping = new GenericMapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200A0, All = 0x1F01FF };
                    var privileges = new byte[256];
                    var privilegesLength = privileges.Length;
                    return AccessCheck(descriptor, token, MaximumAllowedAccess, ref mapping, privileges, ref privilegesLength, out var grantedAccess, out _)
                        ? grantedAccess
                        : null;
                }
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    // The extended-length form of a path, for raw Win32 calls that do not add it themselves.
    internal static string ExtendedLengthPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return full;
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private const int FileDeleteChild = 0x0040;
    private const int FileWriteAttributes = 0x0100;
    private const int Delete = 0x10000;
    private const int ReadControl = 0x20000;
    private const int MaximumAllowedAccess = 0x02000000;
    private const int FileFlagBackupSemantics = 0x02000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int SeFileObject = 1;
    private const int OwnerGroupDaclInformation = 0x1 | 0x2 | 0x4;
    private const int TokenDuplicate = 0x0002;
    private const int TokenQuery = 0x0008;
    private const int SecurityImpersonation = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        public int Read;
        public int Write;
        public int Execute;
        public int All;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, int desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        FileMode creationDisposition, int flagsAndAttributes, IntPtr templateFile);

    [DllImport("advapi32.dll")]
    private static extern int GetSecurityInfo(
        SafeFileHandle handle, int objectType, int securityInfo,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateToken(SafeAccessTokenHandle existingToken, int impersonationLevel, out SafeAccessTokenHandle duplicateToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AccessCheck(
        IntPtr securityDescriptor, SafeAccessTokenHandle clientToken, int desiredAccess, ref GenericMapping genericMapping,
        byte[] privilegeSet, ref int privilegeSetLength, out int grantedAccess, out bool accessStatus);
}
