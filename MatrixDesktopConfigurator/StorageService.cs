using System;
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
                return new ConfiguratorState();
            }

            var json = File.ReadAllText(StoragePath);
            var state = JsonSerializer.Deserialize<ConfiguratorState>(json, _jsonOptions);
            if (state is null)
            {
                // Deserialize returning null means the file existed but its top-level
                // JSON was literal `null`. Treat as fresh state but make it loud so the
                // user understands their presets aren't gone — they're just unreadable.
                Logger.Warn($"Configurator state file deserialized to null. Path='{StoragePath}'. Starting from fresh state.");
                return new ConfiguratorState();
            }

            state.SchemaVersion = Math.Max(1, state.SchemaVersion);
            state.PresetSeedVersion = Math.Max(0, state.PresetSeedVersion);
            state.LastDraft ??= [];
            state.UserPresets ??= [];
            return state;
        }
        catch (Exception ex)
        {
            // Corrupt JSON, encoding error, partial write — previously this was a
            // silent fallback that quietly dropped any user presets. Log so the user
            // can investigate (and potentially restore from the preserved file).
            Logger.Error($"Failed to load configurator state from '{StoragePath}'. Starting from fresh state.", ex);
            return new ConfiguratorState();
        }
    }

    public void Save(ConfiguratorState state)
    {
        var directory = Path.GetDirectoryName(StoragePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(StoragePath, JsonSerializer.Serialize(state, _jsonOptions));
    }

    public static JsonObject CloneObject(JsonObject? source)
    {
        if (source is null)
        {
            return [];
        }

        return JsonNode.Parse(source.ToJsonString())?.AsObject() ?? [];
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
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            return stream.CanWrite;
        }
        catch
        {
            return false;
        }
    }
}
