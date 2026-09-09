using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatrixDesktop.Shared;

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
        var portablePath = Path.Combine(AppContext.BaseDirectory, FileName);
        if (CanWriteStorageFile(portablePath))
        {
            return (portablePath, true);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.GetTempPath();
        }

        return (Path.Combine(appData, "MatrixDesktop", "Configurator", FileName), false);
    }

    private static bool CanWriteStorageFile(string path)
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
            // first run. Probe without leaving anything behind: an existing file is proved
            // writable by opening it, and a missing one is proved by a uniquely named temp
            // probe that is deleted again.
            if (File.Exists(path))
            {
                using var existing = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                return existing.CanWrite;
            }

            // Fixed name, and deleted in a finally. A GUID name meant a delete that failed,
            // to a scanner holding the freshly closed handle, left one orphan per launch with
            // nothing anywhere to sweep them; and the early return inside the using skipped
            // the delete entirely.
            var probe = Path.Combine(directory, ".write-probe");
            try
            {
                using (var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (!stream.CanWrite)
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                try { File.Delete(probe); } catch { /* swept on the next successful probe */ }
            }
        }
        catch
        {
            return false;
        }
    }
}
